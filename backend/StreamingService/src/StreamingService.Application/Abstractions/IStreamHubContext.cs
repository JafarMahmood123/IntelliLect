namespace StreamingService.Application.Abstractions;

public interface IStreamHubContext
{
    Task NotifyHandRaisedAsync(Guid sessionId, Guid userId, bool isRaised);
    Task NotifyParticipantCountAsync(Guid sessionId, int count);
    Task NotifyStreamStatusChangedAsync(Guid sessionId, string status);
    Task NotifyPublishPolicyChangedAsync(Guid sessionId, bool canPublishAudio, bool canPublishVideo);

    /// <summary>
    /// Broadcasts the session's recording state ("Off"/"Recording"/"Ended") to everyone in the room.
    /// Not just a teacher-UI concern: participants are entitled to know when they are being
    /// recorded, so this reaches students too.
    /// </summary>
    Task NotifyRecordingStateChangedAsync(Guid sessionId, string state);
    Task BroadcastChatMessageAsync(Guid sessionId, Guid userId, string userName, string message);
    Task BroadcastReactionAsync(Guid sessionId, Guid userId, string emoji);
}