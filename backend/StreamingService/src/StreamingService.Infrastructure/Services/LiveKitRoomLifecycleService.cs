using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamingService.Application.Abstractions;
using StreamingService.Infrastructure.Configuration;

namespace StreamingService.Infrastructure.Services;

/// <summary>
/// Closes LiveKit rooms through the server SDK's <see cref="RoomServiceClient"/>, reusing the same
/// API key/secret as token generation and egress. Deleting the room is what forcibly disconnects
/// every remaining participant when a session ends — a client that ignored (or never received)
/// the "session ended" broadcast is still cut off, and because the room is gone it cannot be
/// silently re-created by a stale join token.
/// </summary>
public sealed class LiveKitRoomLifecycleService : IRoomLifecycleService
{
    // The Room (twirp) API is reached over HTTP(S), but LiveKitSettings.Host is the ws(s):// URL
    // the realtime SDK uses — normalised the same way LiveKitEgressClient does.
    private readonly RoomServiceClient _client;
    private readonly ILogger<LiveKitRoomLifecycleService> _logger;

    public LiveKitRoomLifecycleService(
        IOptions<LiveKitSettings> livekit,
        ILogger<LiveKitRoomLifecycleService> logger)
    {
        var settings = livekit.Value;
        _client = new RoomServiceClient(
            ToHttpUrl(settings.Host), settings.ApiKey, settings.ApiSecret, new HttpClient());
        _logger = logger;
    }

    public async Task CloseRoomAsync(string roomName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(roomName)) return;

        await _client.DeleteRoom(new DeleteRoomRequest { Room = roomName });

        _logger.LogInformation("Closed LiveKit room {RoomName}; all participants disconnected.", roomName);
    }

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
