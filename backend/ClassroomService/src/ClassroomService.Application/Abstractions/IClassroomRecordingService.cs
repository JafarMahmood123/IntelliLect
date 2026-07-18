using ClassroomService.Application.DTOs;
using ClassroomService.Application.DTOs.Recording;
using ClassroomService.Domain.Enums;

namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Read-side access to a classroom's session recordings (R-2). Authorization (classroom
/// membership) lives here, in the application layer, so it is consistent and testable.
/// </summary>
public interface IClassroomRecordingService
{
    /// <summary>
    /// Lists the classroom's recordings for a member (teacher or enrolled student), newest first,
    /// optionally filtered by session and/or status. Throws ForbiddenAccessException for a
    /// non-member; KeyNotFoundException if the classroom does not exist.
    /// </summary>
    Task<PagedResult<RecordingSummaryDto>> ListRecordingsAsync(
        Guid classroomId,
        Guid requestingUserId,
        Guid? sessionId,
        RecordingStatus? status,
        int page,
        int pageSize,
        CancellationToken ct = default);

    /// <summary>
    /// Returns a single recording that belongs to the classroom. Throws ForbiddenAccessException
    /// for a non-member; KeyNotFoundException if the recording is unknown or belongs to a
    /// different classroom (no cross-classroom leakage).
    /// </summary>
    Task<RecordingSummaryDto> GetRecordingAsync(
        Guid classroomId,
        Guid recordingId,
        Guid requestingUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Mints a short-lived, GET-only pre-signed download URL for an Available recording (R-3).
    /// Same membership rule as listing. Throws ForbiddenAccessException for a non-member;
    /// KeyNotFoundException if unknown or in a different classroom; ConflictException if the
    /// recording is not Available (Processing/Failed). Never returns the raw s3_key.
    /// </summary>
    Task<DownloadUrlDto> GetDownloadUrlAsync(
        Guid classroomId,
        Guid recordingId,
        Guid requestingUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves an Available recording to its storage target (key + file name + content type) after
    /// the SAME membership/status checks as <see cref="GetDownloadUrlAsync"/>. Used by the streaming
    /// download endpoint, which serves the bytes through the API/gateway instead of a direct S3 link.
    /// </summary>
    Task<FileDownloadTarget> GetDownloadTargetAsync(
        Guid classroomId,
        Guid recordingId,
        Guid requestingUserId,
        CancellationToken ct = default);
}
