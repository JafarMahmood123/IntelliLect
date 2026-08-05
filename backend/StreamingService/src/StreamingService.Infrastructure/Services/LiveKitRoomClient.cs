using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Options;
using StreamingService.Infrastructure.Configuration;

namespace StreamingService.Infrastructure.Services;

/// <summary>
/// Real <see cref="ILiveKitRoomClient"/> over the LiveKit server SDK's
/// <see cref="RoomServiceClient"/>. Reuses the same API key/secret as token generation and egress
/// (<see cref="LiveKitSettings"/>).
///
/// Pure delegation, exactly like <see cref="LiveKitEgressClient"/> — there is no branch here to
/// get wrong, which is the point: everything that decides something lives in
/// <see cref="LiveKitRoomLifecycleService"/>, on the testable side of the seam.
/// </summary>
public sealed class LiveKitRoomClient : ILiveKitRoomClient
{
    // The Room (twirp) API is reached over HTTP(S) at the server-side API endpoint
    // (internal, LAN-IP-independent) — never the browser-facing ws Host.
    private readonly RoomServiceClient _client;

    // Closing a room is on the session-end path, so an unreachable LiveKit must fail fast
    // rather than blocking on the HttpClient's 100s default.
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(5);

    public LiveKitRoomClient(IOptions<LiveKitSettings> livekit)
    {
        var settings = livekit.Value;
        _client = new RoomServiceClient(
            settings.ApiHttpUrl,
            settings.ApiKey,
            settings.ApiSecret,
            new HttpClient { Timeout = ApiTimeout });
    }

    public Task<DeleteRoomResponse> DeleteRoomAsync(DeleteRoomRequest request)
        => _client.DeleteRoom(request);

    public Task<ListParticipantsResponse> ListParticipantsAsync(ListParticipantsRequest request)
        => _client.ListParticipants(request);

    public Task<ParticipantInfo> UpdateParticipantAsync(UpdateParticipantRequest request)
        => _client.UpdateParticipant(request);
}
