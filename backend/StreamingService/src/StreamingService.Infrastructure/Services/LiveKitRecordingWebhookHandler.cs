using IntelliLect.Contracts.Messages;
using Livekit.Server.Sdk.Dotnet;
using MassTransit;
using Microsoft.Extensions.Logging;
using StreamingService.Application.Abstractions;
using StreamingService.Domain.Enums;
using LkFileInfo = Livekit.Server.Sdk.Dotnet.FileInfo;

namespace StreamingService.Infrastructure.Services;

/// <summary>
/// Turns a verified LiveKit egress webhook into a <see cref="SessionRecordingReadyMessage"/> (R-1).
/// Correlates the egress id back to its <c>LiveStream</c> (persisted in R-0) to recover the
/// session/classroom, publishes success or failure, and marks the stream so duplicate webhook
/// deliveries don't re-publish.
/// </summary>
public sealed class LiveKitRecordingWebhookHandler : IRecordingWebhookHandler
{
    // LiveKit fires room_started when the room is first created (first participant joins) — the
    // moment the room actually exists and egress can attach to it.
    private const string RoomStartedEvent = "room_started";
    // LiveKit fires this once an egress reaches a terminal state; EgressInfo.Status says which.
    private const string EgressEndedEvent = "egress_ended";
    private const string RecordingContentType = "video/mp4";

    private readonly ILiveKitWebhookVerifier _verifier;
    private readonly IStreamRepository _streamRepository;
    private readonly IRecordingStarter _recordingStarter;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<LiveKitRecordingWebhookHandler> _logger;

    public LiveKitRecordingWebhookHandler(
        ILiveKitWebhookVerifier verifier,
        IStreamRepository streamRepository,
        IRecordingStarter recordingStarter,
        IPublishEndpoint publishEndpoint,
        ILogger<LiveKitRecordingWebhookHandler> logger)
    {
        _verifier = verifier;
        _streamRepository = streamRepository;
        _recordingStarter = recordingStarter;
        _publishEndpoint = publishEndpoint;
        _logger = logger;
    }

    public async Task HandleAsync(string body, string authHeader, CancellationToken ct = default)
    {
        // Throws WebhookVerificationException on a bad signature -> the endpoint returns 401.
        var webhookEvent = _verifier.Verify(body, authHeader);

        // room_started: the room now exists, so start recording it (R-0). Kept off the
        // session-start request path precisely because the room does not exist there yet.
        if (string.Equals(webhookEvent.Event, RoomStartedEvent, StringComparison.Ordinal))
        {
            await StartRecordingForRoomAsync(webhookEvent, ct);
            return;
        }

        if (webhookEvent.EgressInfo is null ||
            !string.Equals(webhookEvent.Event, EgressEndedEvent, StringComparison.Ordinal))
        {
            _logger.LogDebug("Ignoring non-terminal LiveKit webhook event {Event}.", webhookEvent.Event);
            return;
        }

        var egress = webhookEvent.EgressInfo;

        // Correlate every log in this operation by egress id.
        using var scope = _logger.BeginScope("egress:{EgressId}", egress.EgressId);
        _logger.LogInformation(
            "LiveKit egress webhook received: event {Event}, egress {EgressId}, status {Status}.",
            webhookEvent.Event, egress.EgressId, egress.Status);

        var stream = await _streamRepository.GetByEgressIdAsync(egress.EgressId, ct);
        if (stream is null)
        {
            // Not ours (or the egress id was never persisted) — nothing to correlate, ignore.
            _logger.LogWarning(
                "No LiveStream found for egress {EgressId}; ignoring recording webhook.", egress.EgressId);
            return;
        }

        if (stream.RecordingReadyPublished)
        {
            _logger.LogInformation(
                "Recording-ready already published for egress {EgressId}; ignoring duplicate webhook.",
                egress.EgressId);
            return;
        }

        var message = BuildMessage(stream.SessionId, stream.ClassroomId, egress);
        await _publishEndpoint.Publish(message, ct);

        // Persist the idempotency marker in the same DbContext scope; with the bus outbox the
        // published message is delivered on SaveChanges, so publish + marker commit together.
        stream.RecordingReadyPublished = true;
        await _streamRepository.UpdateAsync(stream, ct);
        await _streamRepository.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Starts room-composite egress for a room that has just gone live, IF the teacher asked for
    /// this session to be recorded. Best-effort: recording is an enhancement, so any failure is
    /// logged and the session continues. Idempotent — a repeat room_started (or a stream that
    /// already has an egress id) is ignored.
    /// </summary>
    private async Task StartRecordingForRoomAsync(WebhookEvent webhookEvent, CancellationToken ct)
    {
        var roomName = webhookEvent.Room?.Name;
        if (string.IsNullOrWhiteSpace(roomName) || !Guid.TryParse(roomName, out var sessionId))
        {
            _logger.LogDebug(
                "Ignoring room_started webhook without a session-id room name ({RoomName}).", roomName);
            return;
        }

        var stream = await _streamRepository.GetBySessionIdAsync(sessionId, false, ct);
        if (stream is null)
        {
            _logger.LogWarning(
                "No LiveStream for room {SessionId} on room_started; not recording.", sessionId);
            return;
        }

        // Recording is opt-in. Off means the teacher never asked for it; Ended means they already
        // stopped it and it must not restart. Neither is an error — this is the normal path for
        // any session that simply is not being recorded.
        if (stream.RecordingState != RecordingState.Recording)
        {
            _logger.LogDebug(
                "Session {SessionId} is not recording ({State}); ignoring room_started.",
                sessionId, stream.RecordingState);
            return;
        }

        // Cheap guard so the common repeat delivery never reaches the claim.
        if (!string.IsNullOrWhiteSpace(stream.EgressId))
        {
            _logger.LogInformation(
                "Recording already running (egress {EgressId}) for session {SessionId}; ignoring room_started.",
                stream.EgressId, sessionId);
            return;
        }

        await _recordingStarter.TryStartAsync(stream, ct);
    }

    private SessionRecordingReadyMessage BuildMessage(Guid sessionId, Guid classroomId, EgressInfo egress)
    {
        if (egress.Status == EgressStatus.EgressComplete)
        {
            var file = ExtractFile(egress);
            var s3Key = FirstNonEmpty(file?.Filename, file?.Location) ?? string.Empty;
            // LiveKit reports file duration in NANOSECONDS (1 tick = 100ns).
            var duration = file is null ? TimeSpan.Zero : TimeSpan.FromTicks(file.Duration / 100);
            var size = file?.Size ?? 0;

            // Privacy (R-5): log ids + size, never the raw s3 key.
            _logger.LogInformation(
                "Publishing recording-ready (success) for session {SessionId}, egress {EgressId} ({SizeBytes} bytes).",
                sessionId, egress.EgressId, size);

            return new SessionRecordingReadyMessage(
                sessionId, classroomId, s3Key, size, duration,
                EgressId: egress.EgressId, ContentType: RecordingContentType, Succeeded: true, Error: null);
        }

        var error = string.IsNullOrWhiteSpace(egress.Error)
            ? $"Egress ended with status {egress.Status}."
            : egress.Error;

        _logger.LogWarning(
            "Publishing recording-ready (failure) for session {SessionId}, egress {EgressId}: {Error}.",
            sessionId, egress.EgressId, error);

        return new SessionRecordingReadyMessage(
            sessionId, classroomId, S3Key: string.Empty, SizeBytes: 0, Duration: TimeSpan.Zero,
            EgressId: egress.EgressId, ContentType: RecordingContentType, Succeeded: false, Error: error);
    }

    private static LkFileInfo? ExtractFile(EgressInfo egress)
    {
        if (egress.FileResults.Count > 0)
        {
            return egress.FileResults[0];
        }

#pragma warning disable CS0612 // legacy single-file result on older LiveKit servers
        return egress.File;
#pragma warning restore CS0612
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrEmpty(v));
}
