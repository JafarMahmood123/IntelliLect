using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using StreamingService.Application.Abstractions;
using StreamingService.Presentation.Hubs;

namespace StreamingService.Presentation.Services;

public sealed class StreamHubContext : IStreamHubContext
{
    private readonly IHubContext<StreamHub, IStreamClient> _hubContext;
    private readonly ILogger<StreamHubContext> _logger;

    public StreamHubContext(
        IHubContext<StreamHub, IStreamClient> hubContext,
        ILogger<StreamHubContext> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task NotifyHandRaisedAsync(Guid sessionId, Guid userId, bool isRaised)
    {
        _logger.LogDebug(
            "Broadcasting hand raise. SessionId: {SessionId}, UserId: {UserId}, IsRaised: {IsRaised}",
            sessionId, userId, isRaised);

        await _hubContext.Clients.Group(sessionId.ToString())
            .ReceiveHandRaise(userId, isRaised);
    }

    public async Task NotifyPublishPolicyChangedAsync(Guid sessionId, bool canPublishAudio, bool canPublishVideo)
    {
        _logger.LogDebug(
            "Broadcasting publish policy change. SessionId: {SessionId}, Audio: {Audio}, Video: {Video}",
            sessionId, canPublishAudio, canPublishVideo);

        await _hubContext.Clients.Group(sessionId.ToString())
            .PublishPolicyChanged(canPublishAudio, canPublishVideo);
    }

    public async Task NotifyParticipantCountAsync(Guid sessionId, int count)
    {
        _logger.LogDebug(
            "Broadcasting participant count. SessionId: {SessionId}, Count: {Count}",
            sessionId, count);

        await _hubContext.Clients.Group(sessionId.ToString())
            .UpdateParticipantCount(count);
    }

    public async Task NotifyStreamStatusChangedAsync(Guid sessionId, string status)
    {
        _logger.LogDebug(
            "Broadcasting stream status change. SessionId: {SessionId}, Status: {Status}",
            sessionId, status);

        await _hubContext.Clients.Group(sessionId.ToString())
            .StreamStatusChanged(status);
    }

    public async Task BroadcastChatMessageAsync(Guid sessionId, Guid userId, string userName, string message)
    {
        _logger.LogDebug(
            "Broadcasting chat message. SessionId: {SessionId}, UserId: {UserId}",
            sessionId, userId);

        await _hubContext.Clients.Group(sessionId.ToString())
            .ReceiveChatMessage(userId, userName, message);
    }

    public async Task BroadcastReactionAsync(Guid sessionId, Guid userId, string emoji)
    {
        _logger.LogDebug(
            "Broadcasting reaction. SessionId: {SessionId}, UserId: {UserId}",
            sessionId, userId);

        await _hubContext.Clients.Group(sessionId.ToString())
            .ReceiveReaction(userId, emoji);
    }
}
