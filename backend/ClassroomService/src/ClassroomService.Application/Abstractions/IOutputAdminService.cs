using ClassroomService.Application.DTOs.Output;

namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Super-admin management of session recordings and summaries: a combined, filterable listing and
/// deletion of a single output. Deletion marks the output PendingDeletion, removes its object-store
/// files, then its row — so a file-delete failure halts with the output left PendingDeletion and the
/// deletion can be re-run to resume without stranding a file (6ب); a missing file is treated as
/// already-deleted (6أ).
/// </summary>
public interface IOutputAdminService
{
    Task<AdminOutputPage> GetOutputsAsync(
        string? search, string? type, string? status, Guid? classroomId, int page, int pageSize, CancellationToken ct = default);

    /// <exception cref="System.ArgumentException">The reason is missing (4أ).</exception>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">The recording does not exist (5أ).</exception>
    /// <exception cref="Exceptions.ConflictException">The session is currently live (5ب).</exception>
    Task<OutputDeletionResult> DeleteRecordingAsync(Guid recordingId, string reason, CancellationToken ct = default);

    /// <exception cref="System.ArgumentException">The reason is missing (4أ).</exception>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">The summary does not exist (5أ).</exception>
    /// <exception cref="Exceptions.ConflictException">The session is currently live (5ب).</exception>
    Task<OutputDeletionResult> DeleteSummaryAsync(Guid summaryId, string reason, CancellationToken ct = default);

    /// <summary>
    /// Super admin forces a summary to be rebuilt, from ANY state except PendingDeletion.
    /// </summary>
    /// <remarks>
    /// Deliberately less restrictive than the teacher's regenerate, which only accepts Failed:
    /// this is the operator escape hatch, so it must be able to rescue a summary stuck in
    /// Generating (a request lost before the outbox existed) and to rebuild an Available one after
    /// a prompt fix. PendingDeletion is still refused — re-requesting would race the file deletion
    /// and could orphan objects in S3.
    /// </remarks>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">The summary does not exist.</exception>
    /// <exception cref="Exceptions.ConflictException">The summary is being deleted.</exception>
    Task<OutputDeletionResult> RegenerateSummaryAsync(Guid summaryId, CancellationToken ct = default);
}
