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
}
