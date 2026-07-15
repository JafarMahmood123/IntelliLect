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
