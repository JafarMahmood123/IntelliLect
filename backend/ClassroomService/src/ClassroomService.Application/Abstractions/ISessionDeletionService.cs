using ClassroomService.Application.DTOs.Session;

namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Super-admin deletion of a single session and its outputs (recording, summary, transcript), with
/// an impact preview. The preview (step 3) is read-only; the deletion (steps 5-6) marks the session
/// PendingDeletion, then purges its outputs object-before-row, so a failure halts with the session
/// left PendingDeletion and the deletion can be re-run to resume (6ب).
/// </summary>
public interface ISessionDeletionService
{
    /// <returns>The deletion impact, or null if the session does not exist (5أ).</returns>
    Task<SessionDeletionImpact?> GetImpactAsync(Guid sessionId, CancellationToken ct = default);

    /// <exception cref="System.ArgumentException">The reason/confirmation is missing (4أ).</exception>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">The session does not exist (5أ).</exception>
    /// <exception cref="Exceptions.ConflictException">The session is currently live (5ب).</exception>
    Task<SessionDeletionResult> DeleteAsync(Guid sessionId, string reason, CancellationToken ct = default);
}
