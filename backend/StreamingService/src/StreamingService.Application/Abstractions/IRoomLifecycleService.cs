namespace StreamingService.Application.Abstractions;

/// <summary>
/// Server-side control over the media room itself, as opposed to the tokens that let people in.
/// Closing a room disconnects everyone still connected — this is what actually removes students
/// when a session ends, rather than trusting each browser to leave on its own.
/// </summary>
public interface IRoomLifecycleService
{
    /// <summary>
    /// Closes the room and disconnects every participant. Idempotent: a room that does not exist
    /// (nobody ever joined, or it was already closed) is not an error.
    /// </summary>
    Task CloseRoomAsync(string roomName, CancellationToken ct = default);

    /// <summary>
    /// Applies a student publish policy to everyone currently connected: every STUDENT participant
    /// (identified by role in their LiveKit metadata — the teacher and the AI assistant are left
    /// untouched) has their live publish permissions updated. Revoking a source that a student is
    /// already publishing force-unpublishes that track immediately. Best-effort per participant: a
    /// room that does not exist, or a single failed update, does not throw. Late joiners are handled
    /// separately by the join token, so this only needs to touch already-connected students.
    /// </summary>
    Task ApplyStudentPublishPolicyAsync(
        Guid sessionId,
        bool canPublishAudio,
        bool canPublishVideo,
        CancellationToken ct = default);
}
