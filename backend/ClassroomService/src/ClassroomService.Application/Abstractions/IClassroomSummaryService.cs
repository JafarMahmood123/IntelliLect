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
    /// <summary>
    /// Re-requests a FAILED summary on behalf of its classroom's teacher.
    /// </summary>
    /// <remarks>
    /// Requires classroom OWNERSHIP, not membership: every other method here is a read gated on
    /// membership, but this one spends an LLM run, so a student who can view a summary must not be
    /// able to trigger one.
    /// <para>
    /// Only <c>Failed</c> is regenerable. An <c>Available</c> summary is refused with 409 rather
    /// than overwritten — the S3 keys are deterministic, so a re-run destroys a good summary in
    /// place, and a mis-click should not be able to do that. <c>Generating</c> is refused for the
    /// same reason a double-click should be harmless: one run at a time.
    /// </para>
    /// </remarks>
    /// <exception cref="ForbiddenAccessException">The user is not the classroom's teacher.</exception>
    /// <exception cref="KeyNotFoundException">Unknown summary, or one from another classroom.</exception>
    /// <exception cref="ConflictException">The summary is not in a regenerable state.</exception>
    Task<SummarySummaryDto> RegenerateSummaryAsync(
        Guid classroomId,
        Guid summaryId,
        Guid requestingUserId,
        CancellationToken ct = default);

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

    /// <summary>
    /// Resolves an Available summary artifact (PDF default, "md" for Markdown) to its storage target
    /// after the SAME membership/status checks as <see cref="GetDownloadUrlAsync"/>. Used by the
    /// streaming download endpoint, which serves the bytes through the API/gateway.
    /// </summary>
    Task<FileDownloadTarget> GetDownloadTargetAsync(
        Guid classroomId,
        Guid summaryId,
        Guid requestingUserId,
        string? format,
        CancellationToken ct = default);
}
