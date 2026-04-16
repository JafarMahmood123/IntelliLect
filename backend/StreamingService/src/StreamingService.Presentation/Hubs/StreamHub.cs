using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using StreamingService.Application.Abstractions;

namespace StreamingService.Presentation.Hubs;

[Authorize]
public sealed class StreamHub : Hub<IStreamClient>
{
    public async Task JoinStreamRoom(Guid sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId.ToString());
    }

    public async Task LeaveStreamRoom(Guid sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId.ToString());
    }
}