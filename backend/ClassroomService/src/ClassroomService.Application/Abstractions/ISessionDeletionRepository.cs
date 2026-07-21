using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Persistence for the super-admin session-deletion use-case. Like the classroom-deletion
/// repository, it deletes object-storage keys before the metadata rows that point at them and every
/// operation is idempotent, which is what makes a failed deletion re-runnable (6ب). The transcript
/// lives in LiveAssistantService, so it is not touched here — the service deletes it over HTTP.
/// </summary>
public interface ISessionDeletionRepository
{
    /// <summary>Loads the tracked session (any status) so the service can mark it PendingDeletion
    /// and later remove it. Returns null if it does not exist (5أ).</summary>
    Task<Session?> GetTrackedAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>The session's recording, or null if it has none (6أ).</summary>
    Task<SessionRecording?> GetRecordingAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>The session's summary, or null if it has none (6أ).</summary>
    Task<SessionSummary?> GetSummaryAsync(Guid sessionId, CancellationToken ct = default);

    void RemoveRecording(SessionRecording recording);
    void RemoveSummary(SessionSummary summary);
    void RemoveSession(Session session);

    Task SaveChangesAsync(CancellationToken ct = default);
}
