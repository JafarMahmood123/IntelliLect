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
    /// Base URL of a CUSTOM recording template, or empty for LiveKit's built-in one.
    ///
    /// Room-composite egress does not composite tracks: it opens headless Chrome, loads a web page
    /// and captures it. Pointing that page at our own recorder means the recording contains
    /// whatever the page renders — which is how the teacher's whiteboard annotations reach the
    /// downloaded MP4 at all. They are drawn on a canvas in the browser, so LiveKit's template,
    /// which only knows about tracks, cannot see them.
    ///
    /// EMPTY IS THE SAFE DEFAULT AND MEANS EXACTLY TODAY'S BEHAVIOUR. Taking over the template also
    /// takes over responsibility for what a lesson recording looks like, and a mistake there is
    /// discovered after the lesson rather than during it. Keeping this in configuration means the
    /// way back is an appsettings edit, not a rebuild.
    ///
    /// LiveKit appends <c>?url=</c>, <c>?token=</c> and <c>?layout=</c> and waits for the page to
    /// log START_RECORDING before it captures anything, so a page that fails to load produces a
    /// failed egress rather than a silent hour of blank video.
    ///
    /// Must be reachable FROM THE EGRESS CONTAINER, which is not the URL a browser uses — the same
    /// split that S3:Endpoint carries below.
    /// </summary>
    public string? CustomBaseUrl { get; init; }

    /// <summary>
    /// Output video dimensions and frame rate for the room-composite encode. Deliberately modest
    /// by default (540p @ 10fps): room-composite runs headless Chrome + a GStreamer H.264 encode,
    /// and on a constrained/virtualized host (e.g. Docker Desktop) a heavier encode starves the
    /// pipeline — the audio branch drops samples and the muxer FREEZES at finalization ("pipeline
    /// frozen"), producing a 0-byte failed recording. Lower settings keep enough headroom to flush
    /// the MP4 cleanly on stop. Raise on a beefier host if you want a sharper capture.
    ///
    /// 960x540@10 is not a guess — it is the only combination measured at ZERO dropped buffers on
    /// the dev host (1280x720@15 dropped 809 in 83 seconds, freezing the video 54% of the time).
    /// Note the recording is the half of the system that can afford to be modest: it is watched
    /// afterwards from a file, whereas <c>MediaOptions</c> governs the LIVE stream, which is
    /// judged in real time and is where the resolution budget belongs.
    /// </summary>
    public int Width { get; init; } = 960;
    public int Height { get; init; } = 540;
    public int Framerate { get; init; } = 10;

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
    /// is tuned for 1080p30 and is wildly loose for the modest lecture capture above: it produced
    /// ~2.3 Mbps / ~1 GB per hour of mostly-static slide content. 1200 kbps at 540p10 is
    /// comfortable for slides plus a talking head, and 96 kbps is ample for speech (128 is
    /// music-grade). A lower target also means less encoder work, which reduces the pipeline
    /// starvation described above.
    ///
    /// Keep this in step with Width/Height. Bitrate and resolution are one setting in two fields:
    /// raising the resolution without the bitrate spreads the same bits over more area and looks
    /// WORSE than before, and leaving the bitrate high after lowering the resolution spends
    /// encoder work — the exact pressure that freezes the muxer — on bits the picture cannot use.
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
