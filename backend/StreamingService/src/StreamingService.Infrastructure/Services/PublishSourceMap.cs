using Livekit.Server.Sdk.Dotnet;

namespace StreamingService.Infrastructure.Services;

/// <summary>
/// Single source of truth for turning "can publish audio / video" into the concrete LiveKit
/// publish-source lists, in the two shapes the SDK needs: string names for the join token's
/// <see cref="VideoGrants.CanPublishSources"/>, and <see cref="TrackSource"/> enum values for the
/// runtime <see cref="ParticipantPermission.CanPublishSources"/>. Keeping both here guarantees a
/// late joiner's token and a connected participant's live permissions stay in lock-step.
/// </summary>
internal static class PublishSourceMap
{
    // LiveKit's canonical source string names (match the JS/token convention).
    private const string Camera = "camera";
    private const string Microphone = "microphone";
    private const string ScreenShare = "screen_share";
    private const string ScreenShareAudio = "screen_share_audio";

    /// <summary>Source-name list for a join token's <see cref="VideoGrants.CanPublishSources"/>.
    /// The teacher additionally gets screen-share; students are limited to camera/mic.</summary>
    public static List<string> TokenSources(bool canPublishAudio, bool canPublishVideo, bool isTeacher)
    {
        var sources = new List<string>();
        if (canPublishVideo) sources.Add(Camera);
        if (canPublishAudio) sources.Add(Microphone);
        if (isTeacher)
        {
            sources.Add(ScreenShare);
            sources.Add(ScreenShareAudio);
        }
        return sources;
    }

    /// <summary>Runtime <see cref="TrackSource"/> list for a student's live
    /// <see cref="ParticipantPermission.CanPublishSources"/> (students never screen-share).</summary>
    public static IEnumerable<TrackSource> StudentRuntimeSources(bool canPublishAudio, bool canPublishVideo)
    {
        if (canPublishVideo) yield return TrackSource.Camera;
        if (canPublishAudio) yield return TrackSource.Microphone;
    }
}
