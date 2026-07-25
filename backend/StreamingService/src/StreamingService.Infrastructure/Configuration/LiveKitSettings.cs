using StreamingService.Application.Abstractions;

namespace StreamingService.Infrastructure.Configuration;

public sealed class LiveKitSettings : IStreamSettings
{
    public const string SectionName = "LiveKit";
    public string ApiKey { get; init; } = null!;
    public string ApiSecret { get; init; } = null!;

    /// <summary>
    /// The ws(s):// URL handed to browsers in the join response so they connect to the media
    /// server. In LAN dev this must be the host's LAN IP (see livekit.yaml node_ip); it changes
    /// whenever the machine moves networks, which is why it must NOT be used for server-side calls.
    /// </summary>
    public string Host { get; init; } = null!;

    /// <summary>
    /// The http(s):// endpoint this service uses to reach LiveKit's server-side twirp APIs
    /// (Room, Egress). Prefer an address this container can reach directly and that does NOT
    /// depend on the LAN IP — e.g. the internal container name <c>http://livekit-server:7880</c> —
    /// so starting/ending a session never breaks when the host's LAN IP changes. Falls back to
    /// <see cref="Host"/> when unset, preserving the previous single-URL behaviour.
    /// </summary>
    public string? ApiUrl { get; init; }

    public string LiveKitHost => Host;

    /// <summary>
    /// The normalised http(s):// base for the server SDK's twirp clients. Uses <see cref="ApiUrl"/>
    /// when configured, otherwise the (scheme-normalised) browser <see cref="Host"/>.
    /// </summary>
    public string ApiHttpUrl => ToHttpUrl(string.IsNullOrWhiteSpace(ApiUrl) ? Host : ApiUrl);

    // The realtime SDK speaks ws(s):// but the Room/Egress twirp APIs are http(s)://. Normalise
    // once here so every server-side client shares the same conversion.
    private static string ToHttpUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        if (url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            return string.Concat("https://", url.AsSpan("wss://".Length));
        if (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            return string.Concat("http://", url.AsSpan("ws://".Length));
        return url;
    }
}
