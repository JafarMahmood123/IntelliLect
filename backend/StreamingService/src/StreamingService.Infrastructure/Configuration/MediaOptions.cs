using StreamingService.Application.Abstractions;

namespace StreamingService.Infrastructure.Configuration;

/// <summary>
/// LiveKit client media settings, bound from the "Media" section and delivered to the browser in the
/// join response (see <see cref="IMediaSettings"/> for why the server owns these).
/// </summary>
/// <remarks>
/// Every default below was verified against the installed livekit-client bundle, so the "was"
/// comments are the library's real previous behaviour and not a guess. Before this section existed
/// the frontend passed NO options at all, so all of these ran at library defaults.
/// </remarks>
public sealed class MediaOptions : IMediaSettings
{
    public const string SectionName = "Media";

    // ---------------------------------------------------------------------------------------
    // CHANGED from the library defaults — the safe wins.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Was <c>false</c>. Without it, a subscriber pulls FULL resolution for every tile no matter how
    /// small it is rendered, so a grid of students costs the same as a grid of fullscreen videos.
    /// This is the single biggest win as participant count grows.
    ///
    /// Requires tracks be attached through an element the SDK can measure. Safe here: the stage
    /// renders via &lt;VideoTrack&gt; from @livekit/components-react, which attaches internally. If a
    /// tile is ever hand-rolled with a raw &lt;video&gt; element, its track may never start.
    /// </summary>
    public bool AdaptiveStream { get; init; } = true;

    /// <summary>
    /// Was <c>false</c>. Lets the server tell a publisher to stop encoding simulcast layers nobody
    /// is subscribed to. NOTE the benefit SHRINKS as the class grows — with many subscribers someone
    /// is usually watching every layer — so this mostly helps a screen share that students have
    /// minimised. AdaptiveStream is the one that scales.
    /// </summary>
    public bool Dynacast { get; init; } = true;

    /// <summary>
    /// Was <c>15</c> (the h1080fps15 screen-share preset). Slides do not need 15fps, and framerate is
    /// what costs the publisher CPU — resolution is what keeps text readable, so that stays at 1080p.
    /// Raise this if you screen-share video or animation.
    /// </summary>
    public int ScreenShareFramerate { get; init; } = 5;

    /// <summary>
    /// Was <c>1</c>. One failed reconnect attempt used to be enough to drop a participant out of the
    /// lecture entirely — on a flaky or VPN'd connection that is a routine occurrence, not an edge
    /// case. Paired with the client distinguishing a transient drop from a real session end.
    /// </summary>
    public int MaxRetries { get; init; } = 5;

    // ---------------------------------------------------------------------------------------
    // EXPOSED but deliberately left at the current value — flip only after measuring.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// "music" is the library default and the wrong profile for a lecture voice — "speech" would cut
    /// bitrate noticeably. NOT changed yet on purpose: LiveKit is an SFU, so it forwards the
    /// teacher's Opus stream untouched, meaning this is the exact audio the STT transcribes. Change
    /// it only once transcription is confirmed working, or a quality regression becomes impossible
    /// to attribute between the prompt, the boundary detector, and the bitrate.
    /// </summary>
    public string AudioPreset { get; init; } = "music";

    /// <summary>
    /// vp8 is the library default BECAUSE H.264 simulcast support is uneven across browsers (fine in
    /// Chrome, historically broken in Firefox/Safari). H.264 may cut publisher CPU via hardware
    /// encode, but simulcast is exactly what AdaptiveStream selects from — so switching could
    /// undermine the optimization that matters most at scale. Flip only with a real Firefox/Safari
    /// client to test against.
    /// </summary>
    public string VideoCodec { get; init; } = "vp8";

    // ---------------------------------------------------------------------------------------
    // EXPOSED for visibility — DO NOT CHANGE without reading why.
    // ---------------------------------------------------------------------------------------

    /// <summary>The layered encoding AdaptiveStream picks from. Disabling it makes AdaptiveStream pointless.</summary>
    public bool Simulcast { get; init; } = true;

    /// <summary>
    /// Discontinuous transmission: stops sending audio packets during silence. LEAVE THIS TRUE.
    /// The assistant's pause detection accumulates trailing silence from RECEIVED frames — the
    /// audio_level_probe logs confirm silent frames do arrive today (100 frames, 0 speech). If this
    /// ever caused frames to stop during silence, windows would never close on a pause and only the
    /// length cap would fire, which would look like broken boundary detection rather than a media
    /// setting.
    /// </summary>
    public bool Dtx { get; init; } = true;

    /// <summary>Opus RED redundancy — cheap packet-loss resilience on the one stream STT depends on.</summary>
    public bool Red { get; init; } = true;

    /// <summary>
    /// LEAVE THIS FALSE. False means a muted mic keeps publishing silence; true would END the track,
    /// which would gap or terminate the assistant's audio stream on every mute.
    /// </summary>
    public bool StopMicTrackOnMute { get; init; } = false;

    // ---------------------------------------------------------------------------------------
    // Capture / screen share geometry.
    // ---------------------------------------------------------------------------------------

    /// <summary>Camera capture, matching the library's h720 default. Lower to 960x540 for more CPU headroom.</summary>
    public int VideoWidth { get; init; } = 1280;

    public int VideoHeight { get; init; } = 720;
    public int VideoFramerate { get; init; } = 30;

    /// <summary>Screen-share resolution stays at 1080p so slide text remains legible.</summary>
    public int ScreenShareWidth { get; init; } = 1920;

    public int ScreenShareHeight { get; init; } = 1080;

    /// <summary>
    /// bps. The h1080fps15 preset used 2_500_000; at 5fps far less is needed for static slides.
    /// </summary>
    public int ScreenShareMaxBitrate { get; init; } = 1_200_000;

    // ---------------------------------------------------------------------------------------
    // Reconnection timeouts (library defaults; exposed so a slow link can be accommodated).
    // ---------------------------------------------------------------------------------------
    public int PeerConnectionTimeoutMs { get; init; } = 15_000;

    public int WebsocketTimeoutMs { get; init; } = 15_000;
}
