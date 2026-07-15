using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Options;
using StreamingService.Infrastructure.Configuration;

namespace StreamingService.Infrastructure.Services;

/// <summary>
/// Real <see cref="ILiveKitEgressClient"/> over the LiveKit server SDK's
/// <see cref="EgressServiceClient"/>. Reuses the same API key/secret as token generation
/// (<see cref="LiveKitSettings"/>).
/// </summary>
public sealed class LiveKitEgressClient : ILiveKitEgressClient
{
    // EgressServiceClient (SDK 1.2.1) ctor: (host, apiKey, apiSecret, HttpClient).
    private readonly EgressServiceClient _client;

    public LiveKitEgressClient(IOptions<LiveKitSettings> livekit)
    {
        var settings = livekit.Value;
        // The Egress (twirp) API is reached over HTTP(S). LiveKitSettings.Host is a ws(s)://
        // URL used by the realtime SDK for token conventions, so normalise the scheme here.
        _client = new EgressServiceClient(
            ToHttpUrl(settings.Host), settings.ApiKey, settings.ApiSecret, new HttpClient());
    }

    public Task<EgressInfo> StartRoomCompositeEgressAsync(RoomCompositeEgressRequest request)
        => _client.StartRoomCompositeEgress(request);

    public Task<EgressInfo> StopEgressAsync(StopEgressRequest request)
        => _client.StopEgress(request);

    private static string ToHttpUrl(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return host;
        if (host.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            return string.Concat("https://", host.AsSpan("wss://".Length));
        if (host.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            return string.Concat("http://", host.AsSpan("ws://".Length));
        return host;
    }
}
