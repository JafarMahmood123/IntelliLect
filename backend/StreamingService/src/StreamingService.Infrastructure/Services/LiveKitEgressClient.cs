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

    // Recording is best-effort and on the session-start path, so a LiveKit that is slow or
    // unreachable must fail FAST rather than blocking on the HttpClient's 100s default.
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(5);

    public LiveKitEgressClient(IOptions<LiveKitSettings> livekit)
    {
        var settings = livekit.Value;
        // Use the server-side API endpoint (internal, LAN-IP-independent), not the browser Host.
        _client = new EgressServiceClient(
            settings.ApiHttpUrl,
            settings.ApiKey,
            settings.ApiSecret,
            new HttpClient { Timeout = ApiTimeout });
    }

    public Task<EgressInfo> StartRoomCompositeEgressAsync(RoomCompositeEgressRequest request)
        => _client.StartRoomCompositeEgress(request);

    public Task<EgressInfo> StopEgressAsync(StopEgressRequest request)
        => _client.StopEgress(request);
}
