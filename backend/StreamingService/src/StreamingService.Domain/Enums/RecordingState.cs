namespace StreamingService.Domain.Enums;

/// <summary>
/// Whether this session's recording is wanted, running, or finished — the DESIRED state, which is
/// what every recording path now reconciles against. Recording used to be implied by "the stream is
/// live", so a live session without an egress could only mean something had broken; now it is a
/// legitimate state that the teacher chose.
///
/// <see cref="Ended"/> is terminal: stopping is final for the session, so the recording archives as
/// one continuous video rather than a set of fragments that would need stitching. That rule lives
/// here rather than in the UI so the API enforces it too.
///
/// The distinction between <see cref="Off"/> and <see cref="Ended"/> is what a bare bool could not
/// express: with recording defaulting to off, "false" is ambiguous between "not yet" (which the
/// teacher may still turn on) and "already done" (which must never restart).
/// </summary>
public enum RecordingState
{
    /// <summary>Not recording. The teacher may start it. This is the default.</summary>
    Off = 0,

    /// <summary>Recording is wanted. An egress should be running whenever the room exists.</summary>
    Recording = 1,

    /// <summary>Recording ran and was stopped. Terminal — it cannot be restarted.</summary>
    Ended = 2,
}
