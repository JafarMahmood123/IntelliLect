using Livekit.Server.Sdk.Dotnet;

namespace StreamingService.Infrastructure.Services;

/// <summary>
/// Thin seam over the LiveKit <see cref="EgressServiceClient"/> (whose methods are non-virtual
/// and take no interface) so <see cref="LiveKitRecordingEgressService"/> can be unit-tested
/// offline without a live egress server. Deliberately mirrors the SDK method shapes.
/// </summary>
public interface ILiveKitEgressClient
{
    Task<EgressInfo> StartRoomCompositeEgressAsync(RoomCompositeEgressRequest request);

    Task<EgressInfo> StopEgressAsync(StopEgressRequest request);
}
