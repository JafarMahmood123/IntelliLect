namespace StreamingService.Domain.Enums;

/// <summary>Maps the session-creation-time <see cref="StudentParticipationMode"/> onto the two
/// independent runtime publish flags on a stream. Used to seed a new stream's flags; after that
/// the teacher's in-session toggles are the source of truth.</summary>
public static class ParticipationModeExtensions
{
    public static bool AllowsAudio(this StudentParticipationMode mode) =>
        mode is StudentParticipationMode.AudioOnly or StudentParticipationMode.AudioAndVideo;

    public static bool AllowsVideo(this StudentParticipationMode mode) =>
        mode is StudentParticipationMode.AudioAndVideo;
}
