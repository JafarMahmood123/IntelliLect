using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using StreamingService.Application.Abstractions;
using StreamingService.Presentation.Hubs;

namespace StreamingService.Presentation.Services;

public sealed class StreamHubContext : IStreamHubContext
{
    private readonly IHubContext<StreamHub, IStreamClient> _hubContext;
    private readonly IBroadcastMetrics _metrics;
    private readonly ILogger<StreamHubContext> _logger;

    public StreamHubContext(
        IHubContext<StreamHub, IStreamClient> hubContext,
        IBroadcastMetrics metrics,
        ILogger<StreamHubContext> logger)
    {
        _hubContext = hubContext;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// Every broadcast goes to the session's group and is timed the same way (§9.2).
    ///
    /// The timing is two <see cref="Stopwatch"/> reads either side of the fan-out — hundreds of
    /// nanoseconds against a call measured in milliseconds. That ratio is the whole reason it is
    /// safe to leave on in production: an instrument that costs a meaningful fraction of what it
    /// measures reports its own overhead.
    ///
    /// A throwing broadcast records nothing. See <see cref="IBroadcastMetrics"/> for why.
    /// </summary>
    private async Task BroadcastAsync(Guid sessionId, string eventName, Func<IStreamClient, Task> send)
    {
        var startedAt = Stopwatch.GetTimestamp();
        await send(_hubContext.Clients.Group(sessionId.ToString()));
        _metrics.BroadcastCompleted(eventName, Stopwatch.GetElapsedTime(startedAt).TotalSeconds);
    }

    public Task NotifyHandRaisedAsync(Guid sessionId, Guid userId, bool isRaised)
    {
        _logger.LogDebug(
            "Broadcasting hand raise. SessionId: {SessionId}, UserId: {UserId}, IsRaised: {IsRaised}",
            sessionId, userId, isRaised);

        return BroadcastAsync(sessionId, nameof(IStreamClient.ReceiveHandRaise),
            client => client.ReceiveHandRaise(userId, isRaised));
    }

    public Task NotifyPublishPolicyChangedAsync(Guid sessionId, bool canPublishAudio, bool canPublishVideo)
    {
        _logger.LogDebug(
            "Broadcasting publish policy change. SessionId: {SessionId}, Audio: {Audio}, Video: {Video}",
            sessionId, canPublishAudio, canPublishVideo);

        return BroadcastAsync(sessionId, nameof(IStreamClient.PublishPolicyChanged),
            client => client.PublishPolicyChanged(canPublishAudio, canPublishVideo));
    }

    public Task NotifyRecordingStateChangedAsync(Guid sessionId, string state)
    {
        _logger.LogDebug(
            "Broadcasting recording state change. SessionId: {SessionId}, State: {State}",
            sessionId, state);

        return BroadcastAsync(sessionId, nameof(IStreamClient.RecordingStateChanged),
            client => client.RecordingStateChanged(state));
    }

    public Task NotifyQuizChangedAsync(Guid sessionId, Guid quizId, string state)
    {
        _logger.LogDebug(
            "Broadcasting quiz state change. SessionId: {SessionId}, QuizId: {QuizId}, State: {State}",
            sessionId, quizId, state);

        return BroadcastAsync(sessionId, nameof(IStreamClient.QuizChanged),
            client => client.QuizChanged(quizId, state));
    }

    public Task NotifyParticipantCountAsync(Guid sessionId, int count)
    {
        _logger.LogDebug(
            "Broadcasting participant count. SessionId: {SessionId}, Count: {Count}",
            sessionId, count);

        return BroadcastAsync(sessionId, nameof(IStreamClient.UpdateParticipantCount),
            client => client.UpdateParticipantCount(count));
    }

    public Task NotifyStreamStatusChangedAsync(Guid sessionId, string status)
    {
        _logger.LogDebug(
            "Broadcasting stream status change. SessionId: {SessionId}, Status: {Status}",
            sessionId, status);

        return BroadcastAsync(sessionId, nameof(IStreamClient.StreamStatusChanged),
            client => client.StreamStatusChanged(status));
    }

    public Task BroadcastChatMessageAsync(Guid sessionId, Guid userId, string userName, string message)
    {
        _logger.LogDebug(
            "Broadcasting chat message. SessionId: {SessionId}, UserId: {UserId}",
            sessionId, userId);

        return BroadcastAsync(sessionId, nameof(IStreamClient.ReceiveChatMessage),
            client => client.ReceiveChatMessage(userId, userName, message));
    }

    public Task BroadcastReactionAsync(Guid sessionId, Guid userId, string emoji)
    {
        _logger.LogDebug(
            "Broadcasting reaction. SessionId: {SessionId}, UserId: {UserId}",
            sessionId, userId);

        return BroadcastAsync(sessionId, nameof(IStreamClient.ReceiveReaction),
            client => client.ReceiveReaction(userId, emoji));
    }
}
