namespace StreamingService.Infrastructure.Configuration;

/// <summary>
/// Options for LiveKit Room Composite Egress recording (R-0), bound from the "Egress" section.
/// LiveKit writes the finished MP4 directly to S3 using this configuration — the bytes never
/// pass through this service. Recording is an enhancement: <see cref="Enabled"/> (default true)
/// lets a deployment run sessions without it.
/// </summary>
public sealed class EgressOptions
{
    public const string SectionName = "Egress";

    /// <summary>Feature flag. When false, egress is skipped entirely and sessions run unrecorded.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Object-key pattern for the recording, e.g. "recordings/{room_name}/{time}.mp4".
    /// Supported tokens: {room_name}, {time}. Rendered by <c>EgressKeyTemplate</c>.
    /// </summary>
    public string KeyTemplate { get; init; } = "recordings/{room_name}/{time}.mp4";

    /// <summary>
    /// Room-composite layout: "speaker" follows the active speaker, "grid" gives every participant
    /// an equal tile. This is the single biggest determinant of what the recording SHOWS — in a
    /// lecture with screen share it decides whether the slides or the talking head are captured —
    /// so it is configuration, not a constant.
    /// </summary>
    public string Layout { get; init; } = "speaker";

    /// <summary>
    /// Output video dimensions and frame rate for the room-composite encode. Deliberately modest
    /// by default (720p @ 15fps): room-composite runs headless Chrome + a GStreamer H.264 encode,
    /// and on a constrained/virtualized host (e.g. Docker Desktop) a heavier encode starves the
    /// pipeline — the audio branch drops samples and the muxer FREEZES at finalization ("pipeline
    /// frozen"), producing a 0-byte failed recording. Lower settings keep enough headroom to flush
    /// the MP4 cleanly on stop. Raise on a beefier host if you want a sharper capture.
    /// </summary>
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public int Framerate { get; init; } = 15;

    /// <summary>
    /// Drops the video branch entirely and records audio only. The cheapest way to guarantee a
    /// usable artifact on a host that cannot sustain the Chrome + H.264 composite.
    /// </summary>
    public bool AudioOnly { get; init; }

    /// <summary>
    /// Encode overrides. UNITS: the bitrates are KILObits per second, NOT bits — LiveKit's own
    /// defaults are video 4500 and audio 128 (see EncodingOptions in LivekitApi.xml), which only
    /// make sense read as kbps. Writing 2_500_000 here asks for 2.5 Tbps, not 2.5 Mbps.
    /// KeyFrameInterval is in seconds.
    ///
    /// These carry explicit defaults rather than null because LiveKit's unset default (4500 kbps)
    /// is tuned for 1080p30 and is wildly loose for the modest lecture capture below: it produced
    /// ~2.3 Mbps / ~1 GB per hour of mostly-static slide content. 1200 kbps at 720p15 is
    /// comfortable for slides plus a talking head, and 96 kbps is ample for speech (128 is
    /// music-grade). A lower target also means less encoder work, which reduces the pipeline
    /// starvation described above.
    ///
    /// Still nullable so an explicit null means "leave LiveKit's default alone". Unset values must
    /// never be forwarded: protobuf scalars are non-nullable, so a 0 on the wire is
    /// indistinguishable from unset and would replace a sane default with an invalid value.
    ///
    /// Reach for these before lowering <see cref="Width"/>/<see cref="Height"/> again: quality and
    /// pipeline stability track bitrate far more closely than resolution, and dropping resolution
    /// is what costs slide legibility.
    /// </summary>
    public int? VideoBitrate { get; init; } = 1200;
    public int? AudioBitrate { get; init; } = 96;
    public double? KeyFrameInterval { get; init; }

    /// <summary>
    /// How long session end waits for LiveKit to finalize and upload the MP4 before closing the
    /// room anyway. StopEgress returns when LiveKit ACCEPTS the stop, not when the file is muxed;
    /// closing the room destroys the composite source, and MP4 writes its index at the END, so
    /// cutting it short truncates the recording. Bounded so a stuck egress cannot block session end
    /// (the caller's HttpClient allows 100s, so this has room).
    /// </summary>
    public int FinalizeWaitSeconds { get; init; } = 20;

    /// <summary>
    /// How often the reconcile loop runs: starts recordings whose <c>room_started</c> webhook was
    /// missed, and stops egresses whose stream is no longer live. Zero or less disables it.
    /// </summary>
    public int ReconcileIntervalSeconds { get; init; } = 30;

    public S3Settings S3 { get; init; } = new();

    /// <summary>Where LiveKit uploads the MP4. Secrets here are never logged.</summary>
    public sealed class S3Settings
    {
        public string Bucket { get; init; } = null!;
        public string Region { get; init; } = null!;
        public string AccessKey { get; init; } = null!;
        public string Secret { get; init; } = null!;

        /// <summary>Optional endpoint for S3-compatible stores (e.g. MinIO). Empty for AWS S3.</summary>
        public string? Endpoint { get; init; }
    }
}
