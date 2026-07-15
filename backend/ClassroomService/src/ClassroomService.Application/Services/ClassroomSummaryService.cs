using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs;
using ClassroomService.Application.DTOs.Recording;
using ClassroomService.Application.DTOs.Summary;
using ClassroomService.Application.Exceptions;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
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
    private readonly ILogger<ClassroomSummaryService> _logger;
    private readonly ISummaryMetrics _metrics;

    public ClassroomSummaryService(
        ISummaryRepository summaryRepository,
        IClassroomRepository classroomRepository,
        IMembershipRepository membershipRepository,
        IRecordingUrlSigner urlSigner,
        ISummaryDownloadSettings downloadSettings,
        ILogger<ClassroomSummaryService> logger,
        ISummaryMetrics metrics)
    {
        _summaryRepository = summaryRepository;
        _classroomRepository = classroomRepository;
        _membershipRepository = membershipRepository;
        _urlSigner = urlSigner;
        _downloadSettings = downloadSettings;
        _logger = logger;
        _metrics = metrics;
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

        var ttlSeconds = _downloadSettings.DownloadUrlTtlSeconds > 0
            ? _downloadSettings.DownloadUrlTtlSeconds
            : DefaultTtlSeconds;
        var ttl = TimeSpan.FromSeconds(ttlSeconds);

        var extension = isMarkdown ? "md" : "pdf";
        var contentType = isMarkdown ? MarkdownContentType : PdfContentType;
        var fileName = $"{summary.SessionId}-summary.{extension}";
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

    /// <summary>
    /// The SAME membership rule as recordings: the classroom's teacher OR an enrolled student may
    /// view its summaries. Missing classroom -> 404; non-member -> 403.
    /// </summary>
    private async Task EnsureMemberAsync(Guid classroomId, Guid userId, CancellationToken ct)
    {
        var classroom = await _classroomRepository.GetByIdAsync(classroomId, ct)
            ?? throw new KeyNotFoundException("Classroom not found.");

        var isMember = classroom.TeacherId == userId
            || await _membershipRepository.IsEnrolledAsync(classroomId, userId, ct);

        if (!isMember)
        {
            throw new ForbiddenAccessException("You are not a member of this classroom.");
        }
    }

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
