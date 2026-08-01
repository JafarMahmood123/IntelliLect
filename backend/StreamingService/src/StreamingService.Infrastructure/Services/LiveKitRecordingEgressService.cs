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
    // Used when Egress:Layout is blank. "speaker" follows the active speaker (the teacher in a
    // lecture); "grid" would capture every participant tile equally.
    private const string FallbackLayout = "speaker";

    // How often the finalize wait re-checks LiveKit. Short enough that a fast finalize is not
    // padded out, long enough not to hammer the API for the full budget.
    private static readonly TimeSpan FinalizePollInterval = TimeSpan.FromSeconds(1);

    // Egress states that mean "still working" — anything else is terminal (Complete, Failed,
    // Aborted, LimitReached).
    private static readonly EgressStatus[] ActiveStatuses =
    [
        EgressStatus.EgressStarting,
        EgressStatus.EgressActive,
        EgressStatus.EgressEnding,
    ];

    // Egress states that can still be STOPPED, which is a narrower question than "still working":
    // EgressEnding has already been told to stop and is finalizing. Asking again achieves nothing
    // and just logs a failure. Now that a teacher can stop recording mid-session, the reconcile
    // pass routinely runs while an egress is finalizing, so this would be a recurring false alarm.
    private static readonly EgressStatus[] StoppableStatuses =
    [
        EgressStatus.EgressStarting,
        EgressStatus.EgressActive,
    ];

    private readonly ILiveKitEgressClient _egressClient;
    private readonly EgressOptions _options;
    private readonly IRecordingMetrics _metrics;
    private readonly ILogger<LiveKitRecordingEgressService> _logger;

    public LiveKitRecordingEgressService(
        ILiveKitEgressClient egressClient,
        IOptions<EgressOptions> options,
        IRecordingMetrics metrics,
        ILogger<LiveKitRecordingEgressService> logger)
    {
        _egressClient = egressClient;
        _options = options.Value;
        _metrics = metrics;
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

        // Modest, explicit encode (see EgressOptions): keeps the Chrome+GStreamer pipeline from
        // starving and freezing the muxer at finalization on constrained/virtualized hosts.
        var encoding = new EncodingOptions
        {
            Width = _options.Width,
            Height = _options.Height,
            Framerate = _options.Framerate,
        };

        // Optional overrides are applied ONLY when configured. Protobuf scalars are non-nullable,
        // so an unconditional assignment would put 0 on the wire for anything left unset — which
        // LiveKit cannot distinguish from a deliberate 0 and which replaces a working default.
        if (_options.VideoBitrate is { } videoBitrate) encoding.VideoBitrate = videoBitrate;
        if (_options.AudioBitrate is { } audioBitrate) encoding.AudioBitrate = audioBitrate;
        if (_options.KeyFrameInterval is { } keyFrameInterval)
        {
            encoding.KeyFrameInterval = keyFrameInterval;
        }

        var request = new RoomCompositeEgressRequest
        {
            RoomName = roomName,
            Layout = string.IsNullOrWhiteSpace(_options.Layout) ? FallbackLayout : _options.Layout,
            AudioOnly = _options.AudioOnly,
            Advanced = encoding,
        };

        // Only when configured. Left unset, LiveKit renders its own template and this records
        // exactly what it recorded before — see EgressOptions.CustomBaseUrl for why that is the
        // default. Assigning unconditionally would put an empty string on the wire, which is NOT
        // the same as unset: it is a template URL that resolves to nothing.
        if (!string.IsNullOrWhiteSpace(_options.CustomBaseUrl))
        {
            request.CustomBaseUrl = _options.CustomBaseUrl;
        }

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

        _metrics.RecordingStarted();

        // Correlate by room + egress id. Privacy (R-5): never log S3 secrets OR the raw object key
        // at info level — the key is internal. The rendered key stays at debug for troubleshooting.
        _logger.LogInformation(
            "Started room-composite egress {EgressId} for room {RoomName} (bucket {Bucket}).",
            info.EgressId, roomName, _options.S3.Bucket);
        _logger.LogDebug("Egress {EgressId} object key: {ObjectKey}.", info.EgressId, objectKey);

        return info.EgressId;
    }

    public async Task StopRecordingAsync(string egressId, CancellationToken ct = default)
    {
        _logger.LogInformation("Requesting stop of recording egress {EgressId}.", egressId);
        await _egressClient.StopEgressAsync(new StopEgressRequest { EgressId = egressId });
    }

    public async Task<bool> WaitForFinalizationAsync(string egressId, CancellationToken ct = default)
    {
        var budget = TimeSpan.FromSeconds(Math.Max(0, _options.FinalizeWaitSeconds));
        if (budget <= TimeSpan.Zero) return false;

        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            EgressStatus? status;
            try
            {
                status = await FindStatusAsync(egressId, ct);
            }
            catch (Exception ex)
            {
                // LiveKit unreachable mid-finalize. Do not hold session end hostage to it.
                _logger.LogWarning(
                    ex, "Could not read status of egress {EgressId} while finalizing.", egressId);
                return false;
            }

            // Gone from the list entirely: LiveKit no longer tracks it, so it is not still writing.
            if (status is null || !ActiveStatuses.Contains(status.Value))
            {
                _logger.LogInformation(
                    "Egress {EgressId} finalized with status {Status}.",
                    egressId, status?.ToString() ?? "unlisted");
                return true;
            }

            try
            {
                await Task.Delay(FinalizePollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        _logger.LogWarning(
            "Egress {EgressId} did not finalize within {Seconds}s; continuing without it. The "
            + "recording may be truncated.",
            egressId, _options.FinalizeWaitSeconds);
        return false;
    }

    public async Task<IReadOnlySet<string>> GetActiveEgressIdsAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled) return new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var response = await _egressClient.ListEgressAsync(new ListEgressRequest { Active = true });
            return response.Items
                .Where(item => StoppableStatuses.Contains(item.Status))
                .Select(item => item.EgressId)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // "Unknown" must not read as "nothing is running", or the reconcile loop would treat
            // every live recording as missing and start a duplicate.
            _logger.LogWarning(ex, "Could not list active egresses; treating state as unknown.");
            throw;
        }
    }

    private async Task<EgressStatus?> FindStatusAsync(string egressId, CancellationToken ct)
    {
        var response = await _egressClient.ListEgressAsync(new ListEgressRequest { EgressId = egressId });
        var match = response.Items.FirstOrDefault(item => item.EgressId == egressId);
        return match?.Status;
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
