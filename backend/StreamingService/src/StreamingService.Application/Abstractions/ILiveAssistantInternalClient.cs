namespace StreamingService.Application.Abstractions;

/// <summary>
/// Internal HTTP client that notifies the LiveAssistantService when a live session
/// starts and ends, so it can join/leave the room and run the teaching-assistant loop.
/// Mirrors ClassroomService's internal-client pattern. Calls are BEST-EFFORT: the
/// caller wraps them in try/catch so a session starts/ends normally even if the
/// assistant is unreachable (the assistant is an enhancement, not a dependency).
/// </summary>
public interface ILiveAssistantInternalClient
{
    /// <summary>
    /// Notify the assistant that a session became live. <paramref name="roomName"/> is the
    /// LiveKit room and <paramref name="teacherIdentity"/> is the teacher's LiveKit
    /// participant identity (the identity the teacher joins under).
    /// </summary>
    Task NotifySessionStartedAsync(
        Guid sessionId,
        Guid classroomId,
        string roomName,
        string teacherIdentity,
        CancellationToken ct = default);

    /// <summary>Notify the assistant that a session ended (tear down its agent).</summary>
    Task NotifySessionEndedAsync(Guid sessionId, CancellationToken ct = default);
}
