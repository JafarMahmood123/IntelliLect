using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using StreamingService.Application.Abstractions;
using System.Security.Claims;

namespace StreamingService.Presentation.Hubs;

[Authorize]
public sealed class StreamHub : Hub<IStreamClient>
{
    private readonly IInteractionService _interactionService;
    private readonly ILogger<StreamHub> _logger;

    public StreamHub(IInteractionService interactionService, ILogger<StreamHub> logger)
    {
        _interactionService = interactionService;
        _logger = logger;
    }

    public override Task OnConnectedAsync()
    {
        var userId = TryGetUserId();
        _logger.LogInformation(
            "Client connected. ConnectionId: {ConnectionId}, UserId: {UserId}",
            Context.ConnectionId, userId);

        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception != null)
        {
            _logger.LogWarning(
                exception,
                "Client disconnected with error. ConnectionId: {ConnectionId}",
                Context.ConnectionId);
        }
        else
        {
            _logger.LogInformation(
                "Client disconnected. ConnectionId: {ConnectionId}",
                Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }

    public async Task JoinStreamRoom(Guid sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId.ToString());

        _logger.LogInformation(
            "Client joined stream room. SessionId: {SessionId}, ConnectionId: {ConnectionId}",
            sessionId, Context.ConnectionId);
    }

    public async Task LeaveStreamRoom(Guid sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId.ToString());

        _logger.LogInformation(
            "Client left stream room. SessionId: {SessionId}, ConnectionId: {ConnectionId}",
            sessionId, Context.ConnectionId);
    }

    public async Task SendChatMessage(Guid sessionId, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        var userId = GetUserId();
        var userName = GetUserName();

        _logger.LogDebug(
            "Hub chat message received. SessionId: {SessionId}, UserId: {UserId}",
            sessionId, userId);

        await _interactionService.SendChatMessageAsync(sessionId, userId, userName, message, Context.ConnectionAborted);
    }

    public async Task SendReaction(Guid sessionId, string emoji)
    {
        if (string.IsNullOrWhiteSpace(emoji)) return;

        var userId = GetUserId();

        _logger.LogDebug(
            "Hub reaction received. SessionId: {SessionId}, UserId: {UserId}",
            sessionId, userId);

        await _interactionService.SendReactionAsync(sessionId, userId, emoji, Context.ConnectionAborted);
    }

    private Guid? TryGetUserId()
    {
        var claim = Context.User?.FindFirst("uid")?.Value;
        if (string.IsNullOrEmpty(claim) || !Guid.TryParse(claim, out var userId))
            return null;
        return userId;
    }

    private Guid GetUserId()
    {
        var userId = TryGetUserId();
        if (userId == null)
        {
            _logger.LogWarning(
                "Invalid user claims on hub connection. ConnectionId: {ConnectionId}",
                Context.ConnectionId);
            throw new UnauthorizedAccessException("Invalid user claims.");
        }
        return userId.Value;
    }

    private string GetUserName()
    {
        return Context.User?.FindFirst(ClaimTypes.Name)?.Value ??
               Context.User?.FindFirst("name")?.Value ??
               "Anonymous";
    }
}
