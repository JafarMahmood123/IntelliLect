using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamingService.Application.Abstractions;
using StreamingService.Infrastructure.Configuration;

namespace StreamingService.Infrastructure.Services;

/// <summary>
/// Drives LiveKit Room Composite Egress to record a room straight to S3 (R-0). Builds an MP4
/// file output with the configured S3 upload target and an object key rendered from
/// <c>Egress:KeyTemplate</c>. Capture only — the "recording ready" flow is R-1.
/// </summary>
public sealed class LiveKitRecordingEgressService : IRecordingEgressService
{
    // Default room-composite layout. "speaker" follows the active speaker (the teacher in a
    // lecture); "grid" would capture every participant tile equally.
    private const string DefaultLayout = "speaker";

    private readonly ILiveKitEgressClient _egressClient;
    private readonly EgressOptions _options;
    private readonly ILogger<LiveKitRecordingEgressService> _logger;

    public LiveKitRecordingEgressService(
        ILiveKitEgressClient egressClient,
        IOptions<EgressOptions> options,
        ILogger<LiveKitRecordingEgressService> logger)
    {
        _egressClient = egressClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string?> StartRoomRecordingAsync(string roomName, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            _logger.LogDebug("Recording egress disabled; skipping for room {RoomName}.", roomName);
            return null;
        }

        var objectKey = EgressKeyTemplate.Render(_options.KeyTemplate, roomName, DateTime.UtcNow);

        var request = new RoomCompositeEgressRequest
        {
            RoomName = roomName,
            Layout = DefaultLayout,
        };

        // A single MP4 written directly to S3 by LiveKit — bytes never touch this service.
        // NOTE (SDK 1.2.1): the repeated `FileOutputs` is the current API; the singular `File`
        // is obsolete. One output is all we need for a room recording.
        request.FileOutputs.Add(new EncodedFileOutput
        {
            FileType = EncodedFileType.Mp4,
            Filepath = objectKey,
            S3 = BuildS3Upload(),
        });

        var info = await _egressClient.StartRoomCompositeEgressAsync(request);

        // Never log S3 secrets — bucket + object key only.
        _logger.LogInformation(
            "Started room-composite egress {EgressId} for room {RoomName} -> s3://{Bucket}/{ObjectKey}.",
            info.EgressId, roomName, _options.S3.Bucket, objectKey);

        return info.EgressId;
    }

    public async Task StopRecordingAsync(string egressId, CancellationToken ct = default)
    {
        _logger.LogInformation("Requesting stop of recording egress {EgressId}.", egressId);
        await _egressClient.StopEgressAsync(new StopEgressRequest { EgressId = egressId });
    }

    private S3Upload BuildS3Upload()
    {
        var s3 = new S3Upload
        {
            AccessKey = _options.S3.AccessKey,
            Secret = _options.S3.Secret,
            Region = _options.S3.Region,
            Bucket = _options.S3.Bucket,
        };

        // Optional endpoint for S3-compatible stores (MinIO, etc.); path-style addressing is
        // required by most non-AWS endpoints.
        if (!string.IsNullOrWhiteSpace(_options.S3.Endpoint))
        {
            s3.Endpoint = _options.S3.Endpoint;
            s3.ForcePathStyle = true;
        }

        return s3;
    }
}
