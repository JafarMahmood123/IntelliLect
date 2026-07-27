namespace StreamingService.Application.Abstractions;

public interface IStreamClient
{
    Task ReceiveHandRaise(Guid userId, bool isRaised);
    Task UpdateParticipantCount(int count);
    Task StreamStatusChanged(string status);
    /// <summary>The teacher changed whether students may publish audio/video. Every client updates
    /// its controls; the media server has already enforced it on connected students.</summary>
    Task PublishPolicyChanged(bool canPublishAudio, bool canPublishVideo);
    Task ReceiveChatMessage(Guid userId, string userName, string message);
    Task ReceiveReaction(Guid userId, string emoji);
}