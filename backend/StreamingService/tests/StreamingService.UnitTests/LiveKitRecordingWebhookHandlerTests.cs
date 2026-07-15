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
}
