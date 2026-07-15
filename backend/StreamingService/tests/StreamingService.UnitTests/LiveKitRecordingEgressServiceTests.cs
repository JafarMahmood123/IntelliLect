using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Options;
using StreamingService.Infrastructure.Configuration;
using StreamingService.Infrastructure.Services;

namespace StreamingService.UnitTests;

public sealed class LiveKitRecordingEgressServiceTests
{
    private static EgressOptions Options(bool enabled = true, string? endpoint = null) => new()
    {
        Enabled = enabled,
        KeyTemplate = "recordings/{room_name}/{time}.mp4",
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
        FakeLiveKitEgressClient client, EgressOptions options)
        => new(client, Microsoft.Extensions.Options.Options.Create(options),
            new RecordingLogger<LiveKitRecordingEgressService>());

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
}
