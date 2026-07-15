namespace ClassroomService.Application.Abstractions;

/// <summary>
/// End-of-life management for recordings (R-4): authorized delete, reconciling stuck Processing
/// rows, and optional retention. All operations remove the S3 object and the metadata row together
/// so there are never dangling references.
/// </summary>
public interface IRecordingLifecycleService
{
    /// <summary>
    /// Deletes a recording (S3 object first, then the row). Only the classroom's teacher or an
    /// admin may delete. Throws ForbiddenAccessException for anyone else; KeyNotFoundException if
    /// the recording is unknown or in a different classroom. If the S3 delete fails hard, the row
    /// is left intact and the error propagates so it can be retried.
    /// </summary>
    Task DeleteRecordingAsync(
        Guid classroomId,
        Guid recordingId,
        Guid requestingUserId,
        bool isAdmin,
        CancellationToken ct = default);

    /// <summary>
    /// Transitions recordings stuck in Processing (older than the configured threshold) to a
    /// terminal Failed state (a missed egress webhook). Returns how many were reconciled.
    /// Idempotent and safe to run repeatedly.
    /// </summary>
    Task<int> ReconcileStuckProcessingAsync(CancellationToken ct = default);

    /// <summary>
    /// If retention is enabled, deletes recordings older than the retention cutoff (S3 object +
    /// row). Returns how many were deleted. A no-op when retention is disabled.
    /// </summary>
    Task<int> ApplyRetentionAsync(CancellationToken ct = default);
}
