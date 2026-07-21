namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Internal HTTP client for LiveAssistantService, which owns session transcripts (النص المفرّغ).
/// ClassroomService owns the session/recording/summary data but not the transcript, so deleting a
/// session's transcript is a cross-service call. Mirrors the other internal clients: sends the
/// shared secret and retries transient faults.
/// </summary>
public interface ILiveAssistantInternalClient
{
    /// <summary>
    /// Number of segments in a session's transcript, or null when there is no transcript (the
    /// service's 404). Used by the deletion impact preview (step 3). Throws on a hard failure so the
    /// caller can report the transcript status as temporarily unavailable rather than as absent.
    /// </summary>
    Task<int?> GetTranscriptSegmentCountAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Delete a session's transcript. Idempotent — a session with no transcript still succeeds
    /// (6أ). Retries transient faults and throws when exhausted, so a hard failure halts the
    /// session deletion with the session left PendingDeletion for a resumable re-run (6ب).
    /// </summary>
    Task DeleteSessionTranscriptAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Delete every transcript for a classroom (used by the classroom-deletion use-case so its
    /// sessions' transcripts do not outlive it). Retries then throws; returns the number removed.
    /// </summary>
    Task<int> DeleteClassroomTranscriptsAsync(Guid classroomId, CancellationToken ct = default);
}
