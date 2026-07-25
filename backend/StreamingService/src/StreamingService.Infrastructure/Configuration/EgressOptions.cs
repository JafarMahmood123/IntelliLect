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
