using Microsoft.AspNetCore.SignalR;
using StreamingService.Application.Abstractions;
using StreamingService.Presentation.Hubs;

namespace StreamingService.Presentation.Services;

public sealed class StreamHubContext : IStreamHubContext
{
    private readonly IHubContext<StreamHub, IStreamClient> _hubContext;

    public StreamHubContext(IHubContext<StreamHub, IStreamClient> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task NotifyHandRaisedAsync(Guid sessionId, Guid userId, bool isRaised)
    {
        await _hubContext.Clients.Group(sessionId.ToString())
            .ReceiveHandRaise(userId, isRaised);
    }

    public async Task NotifyParticipantCountAsync(Guid sessionId, int count)
    {
        await _hubContext.Clients.Group(sessionId.ToString())
            .UpdateParticipantCount(count);
    }

    public async Task NotifyStreamStatusChangedAsync(Guid sessionId, string status)
    {
        await _hubContext.Clients.Group(sessionId.ToString())
            .StreamStatusChanged(status);
    }
}