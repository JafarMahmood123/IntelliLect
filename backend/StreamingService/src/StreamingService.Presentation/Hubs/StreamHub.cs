using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using StreamingService.Application.Abstractions;
using System.Security.Claims;

namespace StreamingService.Presentation.Hubs;

[Authorize]
public sealed class StreamHub : Hub<IStreamClient>
{
    private readonly IInteractionService _interactionService;

    public StreamHub(IInteractionService interactionService)
    {
        _interactionService = interactionService;
    }

    public override Task OnConnectedAsync()
    {
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        return base.OnDisconnectedAsync(exception);
    }

    public async Task JoinStreamRoom(Guid sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId.ToString());
    }

    public async Task LeaveStreamRoom(Guid sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId.ToString());
    }

    public async Task SendChatMessage(Guid sessionId, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        var userId = GetUserId();
        var userName = GetUserName();

        await _interactionService.SendChatMessageAsync(sessionId, userId, userName, message, Context.ConnectionAborted);
    }

    public async Task SendReaction(Guid sessionId, string emoji)
    {
        if (string.IsNullOrWhiteSpace(emoji)) return;

        var userId = GetUserId();

        await _interactionService.SendReactionAsync(sessionId, userId, emoji, Context.ConnectionAborted);
    }

    private Guid GetUserId()
    {
        var claim = Context.User?.FindFirst("uid")?.Value;
        if (string.IsNullOrEmpty(claim) || !Guid.TryParse(claim, out var userId))
            throw new UnauthorizedAccessException("Invalid user claims.");
        return userId;
    }

    private string GetUserName()
    {
        return Context.User?.FindFirst(ClaimTypes.Name)?.Value ??
               Context.User?.FindFirst("name")?.Value ??
               "Anonymous";
    }
}