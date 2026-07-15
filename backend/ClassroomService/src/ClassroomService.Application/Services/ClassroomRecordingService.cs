using System.Globalization;
using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs;
using ClassroomService.Application.DTOs.Recording;
using ClassroomService.Application.Exceptions;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace ClassroomService.Application.Services;

public sealed class ClassroomRecordingService : IClassroomRecordingService
{
    private const string DefaultContentType = "video/mp4";
    private const int DefaultTtlSeconds = 600;

    private readonly IRecordingRepository _recordingRepository;
    private readonly IClassroomRepository _classroomRepository;
    private readonly IMembershipRepository _membershipRepository;
    private readonly IRecordingUrlSigner _urlSigner;
    private readonly IRecordingDownloadSettings _downloadSettings;
    private readonly ILogger<ClassroomRecordingService> _logger;

    public ClassroomRecordingService(
        IRecordingRepository recordingRepository,
        IClassroomRepository classroomRepository,
        IMembershipRepository membershipRepository,
        IRecordingUrlSigner urlSigner,
        IRecordingDownloadSettings downloadSettings,
        ILogger<ClassroomRecordingService> logger)
    {
        _recordingRepository = recordingRepository;
        _classroomRepository = classroomRepository;
        _membershipRepository = membershipRepository;
        _urlSigner = urlSigner;
        _downloadSettings = downloadSettings;
        _logger = logger;
    }

    public async Task<PagedResult<RecordingSummaryDto>> ListRecordingsAsync(
        Guid classroomId,
        Guid requestingUserId,
        Guid? sessionId,
        RecordingStatus? status,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        await EnsureMemberAsync(classroomId, requestingUserId, ct);

        var (items, totalCount) = await _recordingRepository.ListByClassroomAsync(
            classroomId, sessionId, status, page, pageSize, ct);

        var dtos = items.Select(ToDto).ToList();
        return new PagedResult<RecordingSummaryDto>(dtos, totalCount, page, pageSize);
    }

    public async Task<RecordingSummaryDto> GetRecordingAsync(
        Guid classroomId,
        Guid recordingId,
        Guid requestingUserId,
        CancellationToken ct = default)
    {
        await EnsureMemberAsync(classroomId, requestingUserId, ct);

        var recording = await _recordingRepository.GetByIdAsync(recordingId, ct);
        // Unknown, or belongs to another classroom -> 404 (no cross-classroom leakage).
        if (recording is null || recording.ClassroomId != classroomId)
        {
            throw new KeyNotFoundException("Recording not found.");
        }

        return ToDto(recording);
    }

    public async Task<DownloadUrlDto> GetDownloadUrlAsync(
        Guid classroomId,
        Guid recordingId,
        Guid requestingUserId,
        CancellationToken ct = default)
    {
        await EnsureMemberAsync(classroomId, requestingUserId, ct);

        var recording = await _recordingRepository.GetByIdAsync(recordingId, ct);
        // Unknown, or belongs to another classroom -> 404 (no cross-classroom leakage).
        if (recording is null || recording.ClassroomId != classroomId)
        {
            throw new KeyNotFoundException("Recording not found.");
        }

        // Only a finished upload can be downloaded; Processing/Failed -> 409.
        if (recording.Status != RecordingStatus.Available || string.IsNullOrEmpty(recording.S3Key))
        {
            throw new ConflictException("Recording is not available for download.");
        }

        var ttlSeconds = _downloadSettings.DownloadUrlTtlSeconds > 0
            ? _downloadSettings.DownloadUrlTtlSeconds
            : DefaultTtlSeconds;
        var ttl = TimeSpan.FromSeconds(ttlSeconds);

        var fileName = BuildFileName(recording);
        var contentDisposition = $"attachment; filename=\"{fileName}\"";
        var contentType = string.IsNullOrEmpty(recording.ContentType) ? DefaultContentType : recording.ContentType;

        var presigned = await _urlSigner.GeneratePresignedGetUrlAsync(
            recording.S3Key, ttl, contentDisposition, contentType, ct);

        // Audit accountability: who requested a download of which recording, when. The URL is a
        // bearer capability, so it is NEVER logged.
        _logger.LogInformation(
            "Download URL issued for recording {RecordingId} in classroom {ClassroomId} to user {UserId} at {TimestampUtc:o}.",
            recordingId, classroomId, requestingUserId, DateTime.UtcNow);

        return new DownloadUrlDto(presigned.Url, presigned.ExpiresAtUtc);
    }

    /// <summary>
    /// Reuses the platform's membership rule: the classroom's teacher OR an enrolled student may
    /// view its recordings. Missing classroom -> 404; non-member -> 403.
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

    // Friendly, filesystem-safe attachment name derived from the recording's own metadata — no
    // extra lookup needed.
    private static string BuildFileName(SessionRecording r)
        => $"recording-{r.CreatedAtUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.mp4";

    // Metadata only — never exposes S3Key/URL (download URL is minted on demand) or Error detail.
    private static RecordingSummaryDto ToDto(SessionRecording r) => new(
        r.Id,
        r.SessionId,
        r.ClassroomId,
        r.Status.ToString(),
        r.DurationSeconds,
        r.SizeBytes,
        r.ContentType,
        r.CreatedAtUtc,
        r.AvailableAtUtc);
}
