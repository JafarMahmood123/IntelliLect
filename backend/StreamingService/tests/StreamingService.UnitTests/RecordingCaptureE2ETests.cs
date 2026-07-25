using IntelliLect.Contracts.Messages;
using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Logging;
using StreamingService.Infrastructure.Configuration;
using StreamingService.Infrastructure.Services;
using StreamingService.Presentation.Controllers;
using LkFileInfo = Livekit.Server.Sdk.Dotnet.FileInfo;

namespace StreamingService.UnitTests;

/// <summary>
/// Offline e2e for the capture half (R-0 -> R-1): a session goes live (egress started + id
/// persisted), then a mocked, verified egress webhook publishes SessionRecordingReadyMessage.
/// The ClassroomService half (consume -> store -> list -> download -> delete) is covered by
/// RecordingLifecycleE2ETests; the two meet at the SessionRecordingReadyMessage contract.
/// </summary>
public sealed class RecordingCaptureE2ETests
{
    private const string EgressId = "EG_e2e";

    private static WebhookEvent EgressEnded(EgressStatus status, long size = 0, long durationNs = 0, string? error = null)
    {
        var info = new EgressInfo { EgressId = EgressId, RoomName = "room", Status = status };
        if (error is not null) info.Error = error;
        if (status == EgressStatus.EgressComplete)
        {
            info.FileResults.Add(new LkFileInfo { Filename = "recordings/room/f.mp4", Size = size, Duration = durationNs });
        }
        return new WebhookEvent { Event = "egress_ended", EgressInfo = info };
    }

    private static WebhookEvent RoomStarted(Guid sessionId)
        => new() { Event = "room_started", Room = new Room { Name = sessionId.ToString() } };

    [Fact]
    public async Task Full_capture_flow_persists_egress_id_then_publishes_recording_ready()
    {
        var sessionId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();
        var repo = new FakeStreamRepository();
        var egress = new FakeRecordingEgressService(egressId: EgressId);

        // Step 0: session goes live -> stream row persisted. Egress is NOT started here: the
        // LiveKit room does not exist yet, so starting egress now would 404.
        var streamController = new InternalStreamsController(
            repo, new RecordingLiveAssistantClient(), egress, new FakeRoomLifecycleService(),
            new RecordingStreamHubContext(), new RecordingLogger<InternalStreamsController>());
        await streamController.InitializeStream(
            new InitializeStreamRequest(sessionId, classroomId, Guid.NewGuid(), default), default);
        Assert.Null(repo.Find(sessionId)!.EgressId);
        Assert.Equal(0, egress.StartCalls);

        // Step 1 (R-0): room_started webhook -> egress started, id persisted on the LiveStream.
        var startHandler = new LiveKitRecordingWebhookHandler(
            new FakeLiveKitWebhookVerifier(RoomStarted(sessionId)),
            repo, egress, new FakePublishEndpoint(), new RecordingLogger<LiveKitRecordingWebhookHandler>());
        await startHandler.HandleAsync("body", "auth");
        Assert.Equal(EgressId, repo.Find(sessionId)!.EgressId);
        Assert.Equal(1, egress.StartCalls);

        // Step 2 (R-1): a verified egress-complete webhook publishes the recording-ready event.
        var publish = new FakePublishEndpoint();
        var handler = new LiveKitRecordingWebhookHandler(
            new FakeLiveKitWebhookVerifier(EgressEnded(EgressStatus.EgressComplete, size: 4096, durationNs: 8_000_000_000)),
            repo, egress, publish, new RecordingLogger<LiveKitRecordingWebhookHandler>());

        await handler.HandleAsync("body", "auth");

        var msg = publish.LastOf<SessionRecordingReadyMessage>();
        Assert.NotNull(msg);
        Assert.Equal(sessionId, msg!.SessionId);
        Assert.Equal(classroomId, msg.ClassroomId);
        Assert.Equal(EgressId, msg.EgressId);
        Assert.True(msg.Succeeded);
        Assert.Equal(4096, msg.SizeBytes);
        Assert.Equal(TimeSpan.FromSeconds(8), msg.Duration);
    }

    [Fact]
    public async Task Failure_branch_publishes_failed_recording_ready()
    {
        var sessionId = Guid.NewGuid();
        var repo = new FakeStreamRepository();
        var egress = new FakeRecordingEgressService(egressId: EgressId);
        var streamController = new InternalStreamsController(
            repo, new RecordingLiveAssistantClient(), egress, new FakeRoomLifecycleService(),
            new RecordingStreamHubContext(), new RecordingLogger<InternalStreamsController>());
        await streamController.InitializeStream(
            new InitializeStreamRequest(sessionId, Guid.NewGuid(), Guid.NewGuid(), default), default);

        // room_started -> egress started + id persisted, so the egress_ended below can correlate.
        await new LiveKitRecordingWebhookHandler(
            new FakeLiveKitWebhookVerifier(RoomStarted(sessionId)),
            repo, egress, new FakePublishEndpoint(), new RecordingLogger<LiveKitRecordingWebhookHandler>())
            .HandleAsync("body", "auth");

        var publish = new FakePublishEndpoint();
        var handler = new LiveKitRecordingWebhookHandler(
            new FakeLiveKitWebhookVerifier(EgressEnded(EgressStatus.EgressFailed, error: "encoder crashed")),
            repo, egress, publish, new RecordingLogger<LiveKitRecordingWebhookHandler>());

        await handler.HandleAsync("body", "auth");

        var msg = publish.LastOf<SessionRecordingReadyMessage>();
        Assert.NotNull(msg);
        Assert.False(msg!.Succeeded);
        Assert.Equal("encoder crashed", msg.Error);
    }

    [Fact]
    public async Task Egress_start_never_logs_the_object_key_at_info_level()
    {
        var logger = new RecordingLogger<LiveKitRecordingEgressService>();
        var options = new EgressOptions
        {
            Enabled = true,
            KeyTemplate = "recordings/{room_name}/{time}.mp4",
            S3 = new EgressOptions.S3Settings
            {
                Bucket = "intellilect-files", Region = "us-east-1", AccessKey = "k", Secret = "s",
            },
        };
        var service = new LiveKitRecordingEgressService(
            new FakeLiveKitEgressClient(), Microsoft.Extensions.Options.Options.Create(options),
            new FakeRecordingMetrics(), logger);

        await service.StartRoomRecordingAsync("room-1");

        // Privacy rule (R-5): the rendered object key ("recordings/...") must not appear at INFO.
        var infoMessages = logger.Entries.Where(e => e.Level == LogLevel.Information).Select(e => e.Message);
        Assert.All(infoMessages, m => Assert.DoesNotContain("recordings/", m));
        // ...but the egress id is present for correlation.
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("EG_"));
    }
}
