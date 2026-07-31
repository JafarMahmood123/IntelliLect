using StreamingService.Domain.Entities;

namespace StreamingService.Application.Abstractions;

/// <summary>
/// The one place that starts a recording. Three callers need it — the <c>room_started</c> webhook,
/// the reconcile loop, and the teacher's in-session toggle — and all three must arbitrate through
/// the same database claim, or two of them can start a composite for the same room: double the
/// encode cost on a host that can barely sustain one, plus an orphaned MP4 nothing cleans up.
///
/// The webhook and the reconciler previously carried near-identical copies of this sequence. The
/// toggle would have made three, so it is extracted rather than copied again.
/// </summary>
public interface IRecordingStarter
{
    /// <summary>
    /// Claims the session's recording slot and starts room-composite egress, persisting the real
    /// egress id on success and releasing the claim on failure.
    ///
    /// The caller decides WHETHER recording is wanted (and clears any abandoned claim first); this
    /// only decides whether THIS caller is the one that gets to start it. Never throws — a failed
    /// start is reported through the outcome, because recording is an enhancement and must not take
    /// a session down with it.
    /// </summary>
    Task<RecordingStartOutcome> TryStartAsync(LiveStream stream, CancellationToken ct = default);
}

public enum RecordingStartOutcome
{
    /// <summary>Egress is running and its id is persisted.</summary>
    Started,

    /// <summary>Another caller holds the claim. Nothing was started, and nothing is wrong.</summary>
    ClaimLost,

    /// <summary>Recording is disabled by configuration. The claim was released.</summary>
    Disabled,

    /// <summary>LiveKit rejected or could not be reached. The claim was released so a later pass retries.</summary>
    Failed,
}
