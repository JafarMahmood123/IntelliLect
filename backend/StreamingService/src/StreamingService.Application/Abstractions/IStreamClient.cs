namespace StreamingService.Application.Abstractions;

public interface IStreamClient
{
    Task ReceiveHandRaise(Guid userId, bool isRaised);
    Task UpdateParticipantCount(int count);
    Task StreamStatusChanged(string status);
    Task ReceiveChatMessage(Guid userId, string userName, string message);
}