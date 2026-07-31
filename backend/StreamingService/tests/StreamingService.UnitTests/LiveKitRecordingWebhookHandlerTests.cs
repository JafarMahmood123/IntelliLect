using IntelliLect.Contracts.Messages;
using Livekit.Server.Sdk.Dotnet;
using StreamingService.Domain.Entities;
using StreamingService.Domain.Enums;
using StreamingService.Infrastructure.Services;
using LkFileInfo = Livekit.Server.Sdk.Dotnet.FileInfo;

namespace StreamingService.UnitTests;

public sealed class LiveKitRecordingWebhookHandlerTests
{
    private const string EgressId = "EG_abc123";

    private static LiveStream Stream(Guid sessionId, Guid classroomId, bool published = false) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = sessionId,
        ClassroomId = classroomId,
        TeacherId = Guid.NewGuid(),
        Status = StreamStatus.Ended,
        StreamKey = "k",
        EgressId = EgressId,
        RecordingReadyPublished = published,
    };

    private static WebhookEvent EgressEnded(EgressStatus status, string? filename = null, long sizeBytes = 0, long durationNs = 0, string? error = null)
    {
        var info = new EgressInfo { EgressId = EgressId, RoomName = "room", Status = status };
        if (error is not null) info.Error = error;
        if (filename is not null)
        {
            info.FileResults.Add(new LkFileInfo { Filename = filename, Size = sizeBytes, Duration = durationNs });
        }
        return new WebhookEvent { Event = "egress_ended", EgressInfo = info };
    }

    private static (LiveKitRecordingWebhookHandler Handler, FakePublishEndpoint Publish, FakeStreamRepository Repo)
        Build(WebhookEvent evt, params LiveStream[] seed)
    {
        var repo = new FakeStreamRepository(seed);
        var publish = new FakePublishEndpoint();
        var handler = new LiveKitRecordingWebhookHandler(
            new FakeLiveKitWebhookVerifier(evt),
            repo,
            new FakeRecordingEgressService(egressId: EgressId),
            publish,
            new RecordingLogger<LiveKitRecordingWebhookHandler>());
        return (handler, publish, repo);
    }

    [Fact]
    public async Task EgressComplete_publishes_success_message_with_resolved_session_and_metadata()
    {
        var sessionId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();
        var stream = Stream(sessionId, classroomId);
        // 12 seconds expressed in nanoseconds.
        var evt = EgressEnded(EgressStatus.EgressComplete, filename: "recordings/room/f.mp4", sizeBytes: 999_000, durationNs: 12_000_000_000);
        var (handler, publish, _) = Build(evt, stream);

        await handler.HandleAsync("body", "auth");

        var msg = publish.LastOf<SessionRecordingReadyMessage>();
        Assert.NotNull(msg);
        Assert.Equal(sessionId, msg!.SessionId);
        Assert.Equal(classroomId, msg.ClassroomId);
        Assert.Equal("recordings/room/f.mp4", msg.S3Key);
        Assert.Equal(999_000, msg.SizeBytes);
        Assert.Equal(TimeSpan.FromSeconds(12), msg.Duration);
        Assert.Equal(EgressId, msg.EgressId);
        Assert.True(msg.Succeeded);
        Assert.Equal("video/mp4", msg.ContentType);
        // Idempotency marker persisted.
        Assert.True(stream.RecordingReadyPublished);
    }

    [Fact]
    public async Task EgressFailed_publishes_failure_message_with_error()
    {
        var stream = Stream(Guid.NewGuid(), Guid.NewGuid());
        var evt = EgressEnded(EgressStatus.EgressFailed, error: "encoder crashed");
        var (handler, publish, _) = Build(evt, stream);

        await handler.HandleAsync("body", "auth");

        var msg = publish.LastOf<SessionRecordingReadyMessage>();
        Assert.NotNull(msg);
        Assert.False(msg!.Succeeded);
        Assert.Equal("encoder crashed", msg.Error);
        Assert.Equal(string.Empty, msg.S3Key);
        Assert.True(stream.RecordingReadyPublished);
    }

    [Fact]
    public async Task Unknown_egress_id_is_ignored_gracefully_without_publishing()
    {
        // Seed nothing -> the egress id resolves to no stream.
        var evt = EgressEnded(EgressStatus.EgressComplete, filename: "recordings/x.mp4");
        var (handler, publish, _) = Build(evt);

        await handler.HandleAsync("body", "auth");

        Assert.Empty(publish.Published);
    }

    [Fact]
    public async Task Duplicate_delivery_does_not_double_publish()
    {
        var stream = Stream(Guid.NewGuid(), Guid.NewGuid());
        var evt = EgressEnded(EgressStatus.EgressComplete, filename: "recordings/room/f.mp4", sizeBytes: 1, durationNs: 1_000_000_000);
        var (handler, publish, _) = Build(evt, stream);

        await handler.HandleAsync("body", "auth"); // first delivery publishes + marks
        await handler.HandleAsync("body", "auth"); // duplicate delivery is a no-op

        Assert.Single(publish.Published);
    }

    [Fact]
    public async Task Non_terminal_event_is_ignored()
    {
        var stream = Stream(Guid.NewGuid(), Guid.NewGuid());
        var evt = new WebhookEvent
        {
            Event = "egress_started",
            EgressInfo = new EgressInfo { EgressId = EgressId, Status = EgressStatus.EgressActive },
        };
        var (handler, publish, _) = Build(evt, stream);

        await handler.HandleAsync("body", "auth");

        Assert.Empty(publish.Published);
        Assert.False(stream.RecordingReadyPublished);
    }

    [Fact]
    public async Task Room_started_starts_recording_and_persists_egress_id()
    {
        var sessionId = Guid.NewGuid();
        // A live stream with no egress yet — the room has just been created.
        var stream = new LiveStream
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            ClassroomId = Guid.NewGuid(),
            TeacherId = Guid.NewGuid(),
            Status = StreamStatus.Live,
            StreamKey = "k",
        };
        var repo = new FakeStreamRepository(stream);
        var egress = new FakeRecordingEgressService(egressId: EgressId);
        var handler = new LiveKitRecordingWebhookHandler(
            new FakeLiveKitWebhookVerifier(
                new WebhookEvent { Event = "room_started", Room = new Room { Name = sessionId.ToString() } }),
            repo,
            egress,
            new FakePublishEndpoint(),
            new RecordingLogger<LiveKitRecordingWebhookHandler>());

        await handler.HandleAsync("body", "auth");

        Assert.Equal(1, egress.StartCalls);
        Assert.Equal(EgressId, repo.Find(sessionId)!.EgressId);
    }

    [Fact]
    public async Task Room_started_is_idempotent_when_egress_already_running()
    {
        // Stream() seeds an EgressId, so a repeat room_started must not start a second egress.
        var sessionId = Guid.NewGuid();
        var stream = Stream(sessionId, Guid.NewGuid());
        var repo = new FakeStreamRepository(stream);
        var egress = new FakeRecordingEgressService(egressId: EgressId);
        var handler = new LiveKitRecordingWebhookHandler(
            new FakeLiveKitWebhookVerifier(
                new WebhookEvent { Event = "room_started", Room = new Room { Name = sessionId.ToString() } }),
            repo,
            egress,
            new FakePublishEndpoint(),
            new RecordingLogger<LiveKitRecordingWebhookHandler>());

        await handler.HandleAsync("body", "auth");

        Assert.Equal(0, egress.StartCalls);
    }

    [Fact]
    public async Task Room_started_claims_the_slot_before_calling_livekit()
    {
        // The read-then-write guard above cannot arbitrate two deliveries racing each other: both
        // would see a null egress id and both would start a composite, doubling CPU on a host that
        // can barely sustain one and orphaning an MP4 nothing ever cleans up. The claim is what
        // actually decides, so it must be taken BEFORE the start call.
        var sessionId = Guid.NewGuid();
        var repo = new FakeStreamRepository(LiveStream(sessionId));
        var egress = new FakeRecordingEgressService(egressId: EgressId);
        var handler = RoomStartedHandler(sessionId, repo, egress);

        await handler.HandleAsync("body", "auth");

        Assert.Equal(1, repo.ClaimAttempts);
        Assert.Equal(1, egress.StartCalls);
        Assert.Equal(EgressId, repo.Find(sessionId)!.EgressId);
    }

    [Fact]
    public async Task Losing_the_claim_race_starts_nothing()
    {
        // The delivery that loses the claim must abandon quietly. Without this, two webhook
        // deliveries that both read a null egress id would both start a composite — double CPU on
        // a host that can barely sustain one, and a second MP4 that nothing ever cleans up.
        var sessionId = Guid.NewGuid();
        var repo = new FakeStreamRepository(LiveStream(sessionId)) { FailNextClaim = true };
        var egress = new FakeRecordingEgressService(egressId: EgressId);

        await RoomStartedHandler(sessionId, repo, egress).HandleAsync("body", "auth");

        Assert.Equal(1, repo.ClaimAttempts); // it tried
        Assert.Equal(0, egress.StartCalls);  // and did NOT start a second composite
        Assert.Null(repo.Find(sessionId)!.EgressId);
    }

    [Fact]
    public async Task Room_started_releases_the_claim_when_the_egress_cannot_be_started()
    {
        // Otherwise the placeholder would sit there and the session would look permanently
        // mid-claim, blocking its own recovery.
        var sessionId = Guid.NewGuid();
        var repo = new FakeStreamRepository(LiveStream(sessionId));
        var egress = new FakeRecordingEgressService(throwOnCall: true);

        await RoomStartedHandler(sessionId, repo, egress).HandleAsync("body", "auth");

        Assert.Null(repo.Find(sessionId)!.EgressId);
    }

    private static LiveStream LiveStream(Guid sessionId) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = sessionId,
        ClassroomId = Guid.NewGuid(),
        TeacherId = Guid.NewGuid(),
        Status = StreamStatus.Live,
        StreamKey = "k",
    };

    private static LiveKitRecordingWebhookHandler RoomStartedHandler(
        Guid sessionId, FakeStreamRepository repo, FakeRecordingEgressService egress)
        => new(
            new FakeLiveKitWebhookVerifier(
                new WebhookEvent { Event = "room_started", Room = new Room { Name = sessionId.ToString() } }),
            repo,
            egress,
            new FakePublishEndpoint(),
            new RecordingLogger<LiveKitRecordingWebhookHandler>());
}
