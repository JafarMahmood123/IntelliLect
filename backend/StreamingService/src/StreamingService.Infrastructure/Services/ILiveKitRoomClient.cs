using Livekit.Server.Sdk.Dotnet;

namespace StreamingService.Infrastructure.Services;

/// <summary>
/// Thin seam over the LiveKit <see cref="RoomServiceClient"/> (whose methods are non-virtual and
/// take no interface) so <see cref="LiveKitRoomLifecycleService"/> can be unit-tested offline
/// without a live LiveKit server. Deliberately mirrors the SDK method shapes.
///
/// Same pattern, and same reason, as <see cref="ILiveKitEgressClient"/>: the logic worth testing
/// is not the HTTP call, it is what we decide to send and how we behave when a call fails —
/// which participants are touched, which are left alone, and whether one failure stops the rest.
/// </summary>
public interface ILiveKitRoomClient
{
    Task<DeleteRoomResponse> DeleteRoomAsync(DeleteRoomRequest request);

    Task<ListParticipantsResponse> ListParticipantsAsync(ListParticipantsRequest request);

    Task<ParticipantInfo> UpdateParticipantAsync(UpdateParticipantRequest request);
}
