namespace StreamingService.Application.Abstractions;

public interface IMediaProvider
{
    /// <summary>
    /// Mints a LiveKit join token. Publish rights are expressed per-source: a participant may be
    /// allowed to publish audio, video, both, or neither. The teacher (by role) always gets full
    /// publish rights plus screen-share; students get exactly what the two flags allow.
    /// </summary>
    string GenerateJoinToken(
        Guid sessionId,
        Guid userId,
        string role,
        string displayName,
        bool canPublishAudio,
        bool canPublishVideo);
}
