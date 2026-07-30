using Microsoft.Extensions.Configuration;
using StreamingService.Application.Abstractions;
using StreamingService.Infrastructure.Configuration;

namespace StreamingService.UnitTests;

/// <summary>
/// The "Media" configuration section that drives the browser's LiveKit room.
/// </summary>
/// <remarks>
/// Two things are worth pinning. The four values this feature exists to change must not silently
/// drift back to livekit-client's defaults — with adaptiveStream/dynacast off, a thumbnail-sized tile
/// pulled a full-resolution stream, and at MaxRetries=1 a single failed reconnect ejected a student
/// from a live lecture. And the three do-not-touch settings (Dtx, StopMicTrackOnMute, Simulcast) feed
/// the AI assistant's audio frame stream and its pause detection, so a change there would look like
/// broken boundary detection rather than a media misconfiguration.
/// </remarks>
public sealed class MediaOptionsTests
{
    private static MediaOptions Bind(params (string Key, string Value)[] overrides)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                overrides.Select(o =>
                    new KeyValuePair<string, string?>($"Media:{o.Key}", o.Value)))
            .Build();

        var options = new MediaOptions();
        config.GetSection(MediaOptions.SectionName).Bind(options);
        return options;
    }

    [Fact]
    public void Section_name_matches_the_appsettings_key()
    {
        Assert.Equal("Media", MediaOptions.SectionName);
    }

    [Fact]
    public void Defaults_enable_the_two_subscriber_side_optimizations()
    {
        // livekit-client defaults BOTH of these to false. If this ever reads false, every tile is
        // pulling full resolution again regardless of how small it is drawn.
        var options = new MediaOptions();

        Assert.True(options.AdaptiveStream);
        Assert.True(options.Dynacast);
    }

    [Fact]
    public void Default_retry_budget_is_well_above_the_library_default_of_one()
    {
        // At 1, one failed reconnect attempt dropped the participant out of the session entirely.
        Assert.True(new MediaOptions().MaxRetries > 1);
    }

    [Fact]
    public void Default_screen_share_keeps_resolution_but_cuts_framerate()
    {
        // Slides need readable text (resolution); framerate is what costs the publisher CPU. The
        // library's screen-share preset was 1080p at 15fps.
        var options = new MediaOptions();

        Assert.Equal(1920, options.ScreenShareWidth);
        Assert.Equal(1080, options.ScreenShareHeight);
        Assert.True(options.ScreenShareFramerate < 15);
    }

    [Fact]
    public void Deferred_flips_stay_at_their_pre_measurement_values()
    {
        // Both are intentionally NOT changed yet. AudioPreset: an SFU forwards this exact audio to
        // the transcriber, so changing it before STT is verified makes a quality regression
        // unattributable. VideoCodec: h264 simulcast support is uneven across browsers, and
        // simulcast is what AdaptiveStream selects from.
        var options = new MediaOptions();

        Assert.Equal("music", options.AudioPreset);
        Assert.Equal("vp8", options.VideoCodec);
    }

    [Fact]
    public void Do_not_touch_defaults_are_preserved()
    {
        // Dtx true / StopMicTrackOnMute false keep silent frames flowing to the assistant, which is
        // what its pause detection accumulates. Simulcast true is what AdaptiveStream needs.
        var options = new MediaOptions();

        Assert.True(options.Dtx);
        Assert.False(options.StopMicTrackOnMute);
        Assert.True(options.Simulcast);
        Assert.True(options.Red);
    }

    [Fact]
    public void Configuration_overrides_every_default()
    {
        var options = Bind(
            ("AdaptiveStream", "false"),
            ("Dynacast", "false"),
            ("Simulcast", "false"),
            ("VideoCodec", "h264"),
            ("AudioPreset", "speech"),
            ("Dtx", "false"),
            ("Red", "false"),
            ("StopMicTrackOnMute", "true"),
            ("VideoWidth", "640"),
            ("VideoHeight", "360"),
            ("VideoFramerate", "24"),
            ("ScreenShareWidth", "1280"),
            ("ScreenShareHeight", "720"),
            ("ScreenShareFramerate", "3"),
            ("ScreenShareMaxBitrate", "500000"),
            ("MaxRetries", "9"),
            ("PeerConnectionTimeoutMs", "20000"),
            ("WebsocketTimeoutMs", "21000"));

        Assert.False(options.AdaptiveStream);
        Assert.False(options.Dynacast);
        Assert.False(options.Simulcast);
        Assert.Equal("h264", options.VideoCodec);
        Assert.Equal("speech", options.AudioPreset);
        Assert.False(options.Dtx);
        Assert.False(options.Red);
        Assert.True(options.StopMicTrackOnMute);
        Assert.Equal(640, options.VideoWidth);
        Assert.Equal(360, options.VideoHeight);
        Assert.Equal(24, options.VideoFramerate);
        Assert.Equal(1280, options.ScreenShareWidth);
        Assert.Equal(720, options.ScreenShareHeight);
        Assert.Equal(3, options.ScreenShareFramerate);
        Assert.Equal(500_000, options.ScreenShareMaxBitrate);
        Assert.Equal(9, options.MaxRetries);
        Assert.Equal(20_000, options.PeerConnectionTimeoutMs);
        Assert.Equal(21_000, options.WebsocketTimeoutMs);
    }

    [Fact]
    public void A_partial_section_leaves_the_other_defaults_intact()
    {
        // An operator tuning one value must not silently reset the rest to zero/empty.
        var options = Bind(("ScreenShareFramerate", "10"));

        Assert.Equal(10, options.ScreenShareFramerate);
        Assert.True(options.AdaptiveStream);
        Assert.Equal("vp8", options.VideoCodec);
        Assert.Equal(1920, options.ScreenShareWidth);
    }

    [Fact]
    public void Implements_the_application_port_so_StreamService_never_sees_infrastructure()
    {
        // StreamService depends on IMediaSettings, not MediaOptions — the same port/adapter split as
        // IStreamSettings/LiveKitSettings. Assigning here fails to compile if that is broken.
        IMediaSettings settings = new MediaOptions();

        Assert.True(settings.AdaptiveStream);
    }
}
