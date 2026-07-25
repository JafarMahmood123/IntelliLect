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
    // The Room (twirp) API is reached over HTTP(S) at the server-side API endpoint
    // (internal, LAN-IP-independent) — never the browser-facing ws Host.
    private readonly RoomServiceClient _client;
    private readonly ILogger<LiveKitRoomLifecycleService> _logger;

    // Closing a room is on the session-end path, so an unreachable LiveKit must fail fast
    // rather than blocking on the HttpClient's 100s default.
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(5);

    public LiveKitRoomLifecycleService(
        IOptions<LiveKitSettings> livekit,
        ILogger<LiveKitRoomLifecycleService> logger)
    {
        var settings = livekit.Value;
        _client = new RoomServiceClient(
            settings.ApiHttpUrl,
            settings.ApiKey,
            settings.ApiSecret,
            new HttpClient { Timeout = ApiTimeout });
        _logger = logger;
    }

    public async Task CloseRoomAsync(string roomName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(roomName)) return;

        await _client.DeleteRoom(new DeleteRoomRequest { Room = roomName });

        _logger.LogInformation("Closed LiveKit room {RoomName}; all participants disconnected.", roomName);
    }
}
