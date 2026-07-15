using ClassroomService.Application.DTOs;
using ClassroomService.Application.DTOs.Recording;
using ClassroomService.Application.DTOs.Summary;

namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Read-side access to a classroom's session summaries (S-4). Authorization (classroom membership)
/// lives here, in the application layer — the SAME rule as recordings — so it is consistent and
/// testable. Reuses the recording <see cref="DownloadUrlDto"/> and <see cref="IRecordingUrlSigner"/>.
/// </summary>
public interface IClassroomSummaryService
{
    /// <summary>
    /// Lists the classroom's summaries for a member (teacher or enrolled student), newest first,
    /// optionally filtered by session. Throws ForbiddenAccessException for a non-member;
    /// KeyNotFoundException if the classroom does not exist.
    /// </summary>
    Task<PagedResult<SummarySummaryDto>> ListSummariesAsync(
        Guid classroomId,
        Guid requestingUserId,
        Guid? sessionId,
        int page,
        int pageSize,
        CancellationToken ct = default);

    /// <summary>
    /// Returns a single summary that belongs to the classroom. Throws ForbiddenAccessException for a
    /// non-member; KeyNotFoundException if the summary is unknown or belongs to a different classroom.
    /// </summary>
    Task<SummarySummaryDto> GetSummaryAsync(
        Guid classroomId,
        Guid summaryId,
        Guid requestingUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Mints a short-lived, GET-only pre-signed download URL for an Available summary's PDF or
    /// Markdown (<paramref name="format"/> = "pdf" | "md"; default pdf). Same membership rule as
    /// listing. Throws ForbiddenAccessException for a non-member; KeyNotFoundException if unknown or
    /// in a different classroom; ConflictException if the summary is not Available. Never returns the
    /// raw s3 key.
    /// </summary>
    Task<DownloadUrlDto> GetDownloadUrlAsync(
        Guid classroomId,
        Guid summaryId,
        Guid requestingUserId,
        string? format,
        CancellationToken ct = default);
}
