namespace StreamingService.Application.Abstractions;

/// <summary>
/// Media publishing/subscribing quality settings handed to the browser in the join response.
/// </summary>
/// <remarks>
/// These are the LiveKit client's <c>RoomOptions</c>/<c>RoomConnectOptions</c>, owned by the SERVER
/// rather than the frontend. Vite substitutes <c>import.meta.env</c> at build time, so a frontend
/// config would bake the values into the bundle — every change costing a full frontend rebuild, and
/// with no way to vary quality per session. Delivering them alongside the join token makes them a
/// restart-only change and matches how <see cref="IStreamSettings.LiveKitHost"/> and the
/// students-can-publish policy already reach the client.
///
/// This interface lives in Application so <c>StreamService</c> never imports Infrastructure; the
/// concrete <c>MediaOptions</c> binds the "Media" configuration section. Same port/adapter shape as
/// <see cref="IStreamSettings"/> / <c>LiveKitSettings</c>.
/// </remarks>
public interface IMediaSettings
{
    // --- Subscriber-side bandwidth/CPU ---

    /// <summary>Match the received video layer to the rendered element size, and pause off-screen tracks.</summary>
    bool AdaptiveStream { get; }

    /// <summary>Let the server tell publishers to stop encoding simulcast layers nobody subscribes to.</summary>
    bool Dynacast { get; }

    // --- Publisher-side encoding ---

    /// <summary>Publish multiple quality layers so subscribers can pick one. Required for AdaptiveStream to have anything to choose from.</summary>
    bool Simulcast { get; }

    /// <summary>Primary video codec: vp8 | h264 | vp9 | av1 | h265. The client rejects anything else and falls back.</summary>
    string VideoCodec { get; }

    /// <summary>Opus bitrate profile: telephone | speech | music | musicStereo | musicHighQuality | musicHighQualityStereo.</summary>
    string AudioPreset { get; }

    /// <summary>Discontinuous transmission — stop sending audio packets during silence.</summary>
    bool Dtx { get; }

    /// <summary>Opus RED redundancy, for packet-loss resilience on the audio stream.</summary>
    bool Red { get; }

    /// <summary>Whether muting the mic ends the track (true) or keeps publishing silence (false).</summary>
    bool StopMicTrackOnMute { get; }

    // --- Camera capture ---
    int VideoWidth { get; }
    int VideoHeight { get; }
    int VideoFramerate { get; }

    // --- Screen share encoding ---
    int ScreenShareWidth { get; }
    int ScreenShareHeight { get; }
    int ScreenShareFramerate { get; }
    int ScreenShareMaxBitrate { get; }

    // --- Reconnection ---

    /// <summary>Reconnect attempts before the client gives up and reports a terminal disconnect.</summary>
    int MaxRetries { get; }

    int PeerConnectionTimeoutMs { get; }
    int WebsocketTimeoutMs { get; }
}
