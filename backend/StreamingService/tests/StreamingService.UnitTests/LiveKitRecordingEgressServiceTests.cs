using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Options;
using StreamingService.Infrastructure.Configuration;
using StreamingService.Infrastructure.Services;

namespace StreamingService.UnitTests;

public sealed class LiveKitRecordingEgressServiceTests
{
    private static EgressOptions Options(
        bool enabled = true,
        string? endpoint = null,
        string layout = "speaker",
        int? videoBitrate = null,
        int? audioBitrate = null,
        double? keyFrameInterval = null,
        bool audioOnly = false,
        int finalizeWaitSeconds = 20) => new()
    {
        Enabled = enabled,
        KeyTemplate = "recordings/{room_name}/{time}.mp4",
        Layout = layout,
        VideoBitrate = videoBitrate,
        AudioBitrate = audioBitrate,
        KeyFrameInterval = keyFrameInterval,
        AudioOnly = audioOnly,
        FinalizeWaitSeconds = finalizeWaitSeconds,
        S3 = new EgressOptions.S3Settings
        {
            Bucket = "intellilect-files",
            Region = "us-east-1",
            AccessKey = "testuser",
            Secret = "testpassword123!",
            Endpoint = endpoint,
        },
    };

    private static LiveKitRecordingEgressService CreateService(
        FakeLiveKitEgressClient client, EgressOptions options, FakeRecordingMetrics? metrics = null)
        => new(client, Microsoft.Extensions.Options.Options.Create(options),
            metrics ?? new FakeRecordingMetrics(),
            new RecordingLogger<LiveKitRecordingEgressService>());

    [Fact]
    public async Task StartRoomRecording_increments_started_metric()
    {
        var metrics = new FakeRecordingMetrics();
        var service = CreateService(new FakeLiveKitEgressClient(), Options(), metrics);

        await service.StartRoomRecordingAsync("room-1");

        Assert.Equal(1, metrics.StartedCount);
    }

    [Fact]
    public async Task StartRoomRecording_disabled_does_not_increment_started_metric()
    {
        var metrics = new FakeRecordingMetrics();
        var service = CreateService(new FakeLiveKitEgressClient(), Options(enabled: false), metrics);

        await service.StartRoomRecordingAsync("room-1");

        Assert.Equal(0, metrics.StartedCount);
    }

    [Fact]
    public async Task StartRoomRecording_requests_mp4_to_s3_with_key_from_template_and_returns_egress_id()
    {
        var client = new FakeLiveKitEgressClient { EgressIdToReturn = "EG_abc" };
        var service = CreateService(client, Options());

        var roomName = "room-123";
        var egressId = await service.StartRoomRecordingAsync(roomName);

        Assert.Equal("EG_abc", egressId);

        var request = client.LastStartRequest!;
        Assert.Equal(roomName, request.RoomName);

        // MP4 file output written to the configured S3 bucket.
        var file = Assert.Single(request.FileOutputs);
        Assert.Equal(EncodedFileType.Mp4, file.FileType);
        Assert.Equal("intellilect-files", file.S3.Bucket);
        Assert.Equal("us-east-1", file.S3.Region);
        Assert.Equal("testuser", file.S3.AccessKey);

        // Object key is derived from Egress:KeyTemplate: {room_name}/{time} substituted.
        Assert.StartsWith($"recordings/{roomName}/", file.Filepath);
        Assert.EndsWith(".mp4", file.Filepath);
    }

    [Fact]
    public async Task StartRoomRecording_sets_endpoint_and_path_style_for_s3_compatible_store()
    {
        var client = new FakeLiveKitEgressClient();
        var service = CreateService(client, Options(endpoint: "http://intellilect-s3:9000"));

        await service.StartRoomRecordingAsync("room-1");

        var s3 = client.LastStartRequest!.FileOutputs[0].S3;
        Assert.Equal("http://intellilect-s3:9000", s3.Endpoint);
        Assert.True(s3.ForcePathStyle);
    }

    [Fact]
    public async Task StartRoomRecording_returns_null_and_does_not_call_egress_when_disabled()
    {
        var client = new FakeLiveKitEgressClient();
        var service = CreateService(client, Options(enabled: false));

        var egressId = await service.StartRoomRecordingAsync("room-1");

        Assert.Null(egressId);
        Assert.Equal(0, client.StartCalls);
    }

    [Fact]
    public async Task StopRecording_stops_the_egress_by_id()
    {
        var client = new FakeLiveKitEgressClient();
        var service = CreateService(client, Options());

        await service.StopRecordingAsync("EG_xyz");

        Assert.Equal("EG_xyz", client.LastStopRequest!.EgressId);
    }

    // --- encode configuration -------------------------------------------------

    [Fact]
    public async Task StartRoomRecording_uses_the_configured_layout()
    {
        var client = new FakeLiveKitEgressClient();
        var service = CreateService(client, Options(layout: "grid"));

        await service.StartRoomRecordingAsync("room-1");

        Assert.Equal("grid", client.LastStartRequest!.Layout);
    }

    [Fact]
    public async Task StartRoomRecording_falls_back_to_speaker_when_layout_is_blank()
    {
        var client = new FakeLiveKitEgressClient();
        var service = CreateService(client, Options(layout: "  "));

        await service.StartRoomRecordingAsync("room-1");

        Assert.Equal("speaker", client.LastStartRequest!.Layout);
    }

    [Fact]
    public async Task StartRoomRecording_omits_optional_encode_settings_when_unset()
    {
        // The trap this guards: protobuf scalars are non-nullable, so forwarding an unset option
        // would put 0 on the wire — indistinguishable from a deliberate 0, and it replaces a
        // working LiveKit default with an invalid value.
        var client = new FakeLiveKitEgressClient();
        var service = CreateService(client, Options());

        await service.StartRoomRecordingAsync("room-1");

        var encoding = client.LastStartRequest!.Advanced;
        Assert.Equal(0, encoding.VideoBitrate);
        Assert.Equal(0, encoding.AudioBitrate);
        Assert.Equal(0d, encoding.KeyFrameInterval);
        Assert.False(client.LastStartRequest.AudioOnly);

        // The always-set trio is still applied.
        Assert.Equal(1280, encoding.Width);
        Assert.Equal(720, encoding.Height);
        Assert.Equal(15, encoding.Framerate);
    }

    [Fact]
    public async Task StartRoomRecording_applies_optional_encode_settings_when_configured()
    {
        var client = new FakeLiveKitEgressClient();
        var service = CreateService(client, Options(
            videoBitrate: 2_500_000, audioBitrate: 128_000, keyFrameInterval: 4, audioOnly: true));

        await service.StartRoomRecordingAsync("room-1");

        var encoding = client.LastStartRequest!.Advanced;
        Assert.Equal(2_500_000, encoding.VideoBitrate);
        Assert.Equal(128_000, encoding.AudioBitrate);
        Assert.Equal(4d, encoding.KeyFrameInterval);
        Assert.True(client.LastStartRequest.AudioOnly);
    }

    // --- finalize wait --------------------------------------------------------

    [Fact]
    public async Task WaitForFinalization_returns_true_once_the_egress_reaches_a_terminal_state()
    {
        // Active -> Ending -> Complete, the sequence a real finalize walks through.
        var client = new FakeLiveKitEgressClient
        {
            StatusSequence =
            [
                EgressStatus.EgressActive,
                EgressStatus.EgressEnding,
                EgressStatus.EgressComplete,
            ],
        };
        var service = CreateService(client, Options(finalizeWaitSeconds: 10));

        Assert.True(await service.WaitForFinalizationAsync("EG_abc"));
        Assert.Equal(3, client.ListCalls); // polled until it settled, not once
    }

    [Fact]
    public async Task WaitForFinalization_returns_true_when_livekit_no_longer_tracks_the_egress()
    {
        // An egress LiveKit has forgotten is certainly not still writing the file.
        var client = new FakeLiveKitEgressClient { StatusSequence = [] };
        var service = CreateService(client, Options());

        Assert.True(await service.WaitForFinalizationAsync("EG_abc"));
    }

    [Fact]
    public async Task WaitForFinalization_gives_up_at_the_deadline_so_session_end_is_never_blocked()
    {
        var client = new FakeLiveKitEgressClient { StatusSequence = [EgressStatus.EgressEnding] };
        var service = CreateService(client, Options(finalizeWaitSeconds: 1));

        Assert.False(await service.WaitForFinalizationAsync("EG_stuck"));
    }

    [Fact]
    public async Task WaitForFinalization_does_not_throw_when_livekit_is_unreachable()
    {
        var client = new FakeLiveKitEgressClient(throwOnCall: true);
        var service = CreateService(client, Options());

        Assert.False(await service.WaitForFinalizationAsync("EG_abc"));
    }

    // --- active egress listing ------------------------------------------------

    [Fact]
    public async Task GetActiveEgressIds_returns_only_non_terminal_egresses()
    {
        var client = new FakeLiveKitEgressClient
        {
            EgressIdToReturn = "EG_live",
            StatusSequence = [EgressStatus.EgressActive],
        };
        var service = CreateService(client, Options());

        var active = await service.GetActiveEgressIdsAsync();

        Assert.Equal(["EG_live"], active);
        Assert.True(client.LastListRequest!.Active);
    }

    [Fact]
    public async Task GetActiveEgressIds_throws_when_livekit_is_unreachable()
    {
        // Must NOT degrade to an empty set: a caller reconciling state would read that as
        // "nothing is running" and start a duplicate recording for every live session.
        var client = new FakeLiveKitEgressClient(throwOnCall: true);
        var service = CreateService(client, Options());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetActiveEgressIdsAsync());
    }

    [Fact]
    public async Task GetActiveEgressIds_is_empty_when_recording_is_disabled()
    {
        var client = new FakeLiveKitEgressClient();
        var service = CreateService(client, Options(enabled: false));

        Assert.Empty(await service.GetActiveEgressIdsAsync());
        Assert.Equal(0, client.ListCalls);
    }
}
