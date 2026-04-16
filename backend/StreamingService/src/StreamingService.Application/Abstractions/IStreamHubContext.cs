namespace StreamingService.Application.Abstractions;

public interface IStreamHubContext
{
    Task NotifyHandRaisedAsync(Guid sessionId, Guid userId, bool isRaised);
    Task NotifyParticipantCountAsync(Guid sessionId, int count);
    Task NotifyStreamStatusChangedAsync(Guid sessionId, string status);
}