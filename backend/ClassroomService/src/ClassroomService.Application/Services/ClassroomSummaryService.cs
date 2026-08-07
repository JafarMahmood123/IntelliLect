using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs;
using ClassroomService.Application.DTOs.Recording;
using ClassroomService.Application.DTOs.Summary;
using ClassroomService.Application.Exceptions;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
using IntelliLect.Contracts.Messages;
using Microsoft.Extensions.Logging;

namespace ClassroomService.Application.Services;

/// <summary>
/// Read-side session-summary access (S-4). Mirrors <see cref="ClassroomRecordingService"/> and
/// reuses the SAME membership rule and the SAME <see cref="IRecordingUrlSigner"/> — summaries are
/// just a second artifact type (PDF or Markdown) in the same bucket.
/// </summary>
public sealed class ClassroomSummaryService : IClassroomSummaryService
{
    private const string PdfContentType = "application/pdf";
    private const string MarkdownContentType = "text/markdown";
    private const int DefaultTtlSeconds = 600;

    private readonly ISummaryRepository _summaryRepository;
    private readonly IClassroomRepository _classroomRepository;
    private readonly IMembershipRepository _membershipRepository;
    private readonly IRecordingUrlSigner _urlSigner;
    private readonly ISummaryDownloadSettings _downloadSettings;
    private readonly IEventBus _eventBus;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<ClassroomSummaryService> _logger;
    private readonly ISummaryMetrics _metrics;

    public ClassroomSummaryService(
        ISummaryRepository summaryRepository,
        IClassroomRepository classroomRepository,
        IMembershipRepository membershipRepository,
        IRecordingUrlSigner urlSigner,
        ISummaryDownloadSettings downloadSettings,
        IEventBus eventBus,
        IUnitOfWork unitOfWork,
        ILogger<ClassroomSummaryService> logger,
        ISummaryMetrics metrics)
    {
        _summaryRepository = summaryRepository;
        _classroomRepository = classroomRepository;
        _membershipRepository = membershipRepository;
        _urlSigner = urlSigner;
        _downloadSettings = downloadSettings;
        _eventBus = eventBus;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task<SummarySummaryDto> RegenerateSummaryAsync(
        Guid classroomId,
        Guid summaryId,
        Guid requestingUserId,
        CancellationToken ct = default)
    {
        // OWNERSHIP, not membership. Every other method here is a read; this one spends an LLM
        // run, so EnsureMemberAsync would be too permissive — a student could burn generations.
        var classroom = await _classroomRepository.GetByIdAsync(classroomId, ct)
            ?? throw new KeyNotFoundException("Classroom not found.");
        if (classroom.TeacherId != requestingUserId)
        {
            _metrics.AuthzDenied("not_teacher");
            throw new ForbiddenAccessException("Only the classroom's teacher can regenerate a summary.");
        }

        var summary = await _summaryRepository.GetByIdAsync(summaryId, ct);
        // Unknown, or belongs to another classroom -> 404 (no cross-classroom leakage).
        if (summary is null || summary.ClassroomId != classroomId)
        {
            throw new KeyNotFoundException("Summary not found.");
        }

        if (summary.Status != SummaryStatus.Failed)
        {
            // Available is refused rather than overwritten: S3 keys are deterministic, so a re-run
            // replaces a good summary in place and a mis-click would be destructive. Generating is
            // refused so a double-click cannot start two runs.
            throw new ConflictException(
                $"Only a failed summary can be regenerated; this one is {summary.Status}.");
        }

        summary.Status = SummaryStatus.Generating;
        summary.Error = null;

        // Staged on the outbox and committed with the status change, so the classroom can never
        // show Generating for a request that was never actually sent.
        await _eventBus.PublishAsync(
            new SessionSummaryRequestedMessage(
                summary.SessionId,
                summary.ClassroomId,
                RequestedByUserId: requestingUserId,
                Reason: SummaryRequestReasons.ManualTeacher),
            ct);
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Summary {SummaryId} for session {SessionId} re-requested by teacher {UserId}.",
            summaryId, summary.SessionId, requestingUserId);

        return ToDto(summary);
    }

    public async Task<PagedResult<SummarySummaryDto>> ListSummariesAsync(
        Guid classroomId,
        Guid requestingUserId,
        Guid? sessionId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        await EnsureMemberAsync(classroomId, requestingUserId, ct);

        var (items, totalCount) = await _summaryRepository.ListByClassroomAsync(
            classroomId, sessionId, page, pageSize, ct);

        var dtos = items.Select(ToDto).ToList();
        return new PagedResult<SummarySummaryDto>(dtos, totalCount, page, pageSize);
    }

    public async Task<SummarySummaryDto> GetSummaryAsync(
        Guid classroomId,
        Guid summaryId,
        Guid requestingUserId,
        CancellationToken ct = default)
    {
        await EnsureMemberAsync(classroomId, requestingUserId, ct);

        var summary = await _summaryRepository.GetByIdAsync(summaryId, ct);
        // Unknown, or belongs to another classroom -> 404 (no cross-classroom leakage).
        if (summary is null || summary.ClassroomId != classroomId)
        {
            throw new KeyNotFoundException("Summary not found.");
        }

        return ToDto(summary);
    }

    public async Task<DownloadUrlDto> GetDownloadUrlAsync(
        Guid classroomId,
        Guid summaryId,
        Guid requestingUserId,
        string? format,
        CancellationToken ct = default)
    {
        var (objectKey, fileName, contentType, extension) =
            await ResolveAvailableSummaryAsync(classroomId, summaryId, requestingUserId, format, ct);

        var ttlSeconds = _downloadSettings.DownloadUrlTtlSeconds > 0
            ? _downloadSettings.DownloadUrlTtlSeconds
            : DefaultTtlSeconds;
        var ttl = TimeSpan.FromSeconds(ttlSeconds);

        var contentDisposition = $"attachment; filename=\"{fileName}\"";

        var presigned = await _urlSigner.GeneratePresignedGetUrlAsync(
            objectKey, ttl, contentDisposition, contentType, ct);

        _metrics.DownloadUrlIssued(extension);

        // Audit accountability: who requested which summary artifact, when. The URL is a bearer
        // capability, so it is NEVER logged.
        _logger.LogInformation(
            "Summary download URL issued for summary {SummaryId} ({Format}) in classroom {ClassroomId} to user {UserId} at {TimestampUtc:o}.",
            summaryId, extension, classroomId, requestingUserId, DateTime.UtcNow);

        return new DownloadUrlDto(presigned.Url, presigned.ExpiresAtUtc);
    }

    public async Task<FileDownloadTarget> GetDownloadTargetAsync(
        Guid classroomId,
        Guid summaryId,
        Guid requestingUserId,
        string? format,
        CancellationToken ct = default)
    {
        var (objectKey, fileName, contentType, extension) =
            await ResolveAvailableSummaryAsync(classroomId, summaryId, requestingUserId, format, ct);

        _metrics.DownloadUrlIssued(extension);

        // Audit accountability: who downloaded which summary artifact, when.
        _logger.LogInformation(
            "Summary {SummaryId} ({Format}) in classroom {ClassroomId} downloaded by user {UserId} at {TimestampUtc:o}.",
            summaryId, extension, classroomId, requestingUserId, DateTime.UtcNow);

        return new FileDownloadTarget(objectKey, fileName, contentType);
    }

    /// <summary>
    /// Shared download guard: membership (403 + metric on denial), existence/cross-classroom (404),
    /// and Available-with-key (409). Resolves the chosen artifact (PDF default, "md" for Markdown).
    /// </summary>
    private async Task<(string ObjectKey, string FileName, string ContentType, string Extension)> ResolveAvailableSummaryAsync(
        Guid classroomId, Guid summaryId, Guid requestingUserId, string? format, CancellationToken ct)
    {
        try
        {
            await EnsureMemberAsync(classroomId, requestingUserId, ct);
        }
        catch (ForbiddenAccessException)
        {
            _metrics.AuthzDenied("not_member");
            throw;
        }

        var summary = await _summaryRepository.GetByIdAsync(summaryId, ct);
        // Unknown, or belongs to another classroom -> 404 (no cross-classroom leakage).
        if (summary is null || summary.ClassroomId != classroomId)
        {
            throw new KeyNotFoundException("Summary not found.");
        }

        // Default to PDF; only "md" (case-insensitive) selects the Markdown artifact.
        var isMarkdown = string.Equals(format, "md", StringComparison.OrdinalIgnoreCase);
        var objectKey = isMarkdown ? summary.MdS3Key : summary.PdfS3Key;

        // Only a finished summary can be downloaded; Generating/Failed (or a missing key) -> 409.
        if (summary.Status != SummaryStatus.Available || string.IsNullOrEmpty(objectKey))
        {
            throw new ConflictException("Summary is not available for download.");
        }

        var extension = isMarkdown ? "md" : "pdf";
        var contentType = isMarkdown ? MarkdownContentType : PdfContentType;
        var fileName = $"{summary.SessionId}-summary.{extension}";

        return (objectKey, fileName, contentType, extension);
    }

    /// <summary>
    /// The SAME membership rule as recordings: the classroom's teacher OR an enrolled student may
    /// view its summaries. Missing classroom -> 404; non-member -> 403.
    /// </summary>
    /// <summary>
    /// Delegates to <see cref="ClassroomAccess.EnsureMemberAsync"/>. This was a private copy of
    /// that rule, identical to the four others in this service layer; see the reason there.
    /// </summary>
    private Task EnsureMemberAsync(Guid classroomId, Guid userId, CancellationToken ct)
        => ClassroomAccess.EnsureMemberAsync(
            _classroomRepository, _membershipRepository, classroomId, userId, ct);

    // Metadata only — never exposes MdS3Key/PdfS3Key/URL (download URL is minted on demand) or the
    // Error detail.
    private static SummarySummaryDto ToDto(SessionSummary s) => new(
        s.Id,
        s.SessionId,
        s.ClassroomId,
        s.Status.ToString(),
        s.CreatedAtUtc,
        s.AvailableAtUtc);
}
