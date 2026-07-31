namespace StreamingService.Application.DTOs;

public record StreamResponse(
    Guid Id,
    Guid SessionId,
    string Status,
    int ParticipantCount,
    DateTime? StartedAtUtc,
    string JoinToken,
    string LiveKitHost,
    int ParticipationMode,
    bool StudentsCanPublishAudio,
    bool StudentsCanPublishVideo,
    // "Off" / "Recording" / "Ended". Carried on join so someone arriving mid-session sees the
    // recording indicator immediately, rather than only after the next change is broadcast.
    string RecordingState,
    MediaSettingsResponse Media);

/// <summary>
/// Media quality + reconnection settings for the LiveKit client, delivered with the join token.
/// </summary>
/// <remarks>
/// Mirrors <c>IMediaSettings</c>. The client VALIDATES these against its own allowed-value lists and
/// falls back to its bundled defaults on anything unrecognised, so a server-side typo degrades
/// rather than breaking the room.
///
/// Several of these are LiveKit <c>Room</c> CONSTRUCTION options, frozen when the room connects.
/// That is safe because the browser only mounts the room once this payload has arrived.
/// </remarks>
public record MediaSettingsResponse(
    bool AdaptiveStream,
    bool Dynacast,
    bool Simulcast,
    string VideoCodec,
    string AudioPreset,
    bool Dtx,
    bool Red,
    bool StopMicTrackOnMute,
    int VideoWidth,
    int VideoHeight,
    int VideoFramerate,
    int ScreenShareWidth,
    int ScreenShareHeight,
    int ScreenShareFramerate,
    int ScreenShareMaxBitrate,
    int MaxRetries,
    int PeerConnectionTimeoutMs,
    int WebsocketTimeoutMs);
