using Microsoft.Extensions.Logging;
using StreamingService.Application.Abstractions;
using StreamingService.Domain.Entities;
using StreamingService.Domain.Enums;

namespace StreamingService.Infrastructure.Services;

/// <summary>
/// One reconciliation pass that drives reality towards <see cref="RecordingState"/>. Split from the
/// hosted service so the logic is testable without a timer, mirroring how ClassroomService keeps
/// recording maintenance in a service and lets the hosted service do nothing but schedule it.
///
/// This used to infer intent from the stream being live — a live session without an egress could
/// only mean something had broken. Now that recording is opt-in and the teacher toggles it during
/// the session, that inference is wrong: "live and not recording" is usually what was asked for.
/// The desired state is the flag, and this pass repairs both directions against it.
///
/// It matters because every path that changes recording is a single unretried call: the
/// <c>room_started</c> webhook is fire-and-forget, and the teacher's toggle is one HTTP request. If
/// either is lost — this service restarting, briefly unreachable, or throwing — the session would
/// silently disagree with what the teacher asked for. Since this stack is rebuilt constantly, that
/// window is hit often.
/// </summary>
public sealed class EgressReconciler
{
    private readonly IStreamRepository _streams;
    private readonly IRecordingEgressService _egress;
    private readonly IRecordingStarter _recordingStarter;
    private readonly ILogger<EgressReconciler> _logger;

    public EgressReconciler(
        IStreamRepository streams,
        IRecordingEgressService egress,
        IRecordingStarter recordingStarter,
        ILogger<EgressReconciler> logger)
    {
        _streams = streams;
        _egress = egress;
        _recordingStarter = recordingStarter;
        _logger = logger;
    }

    /// <summary>
    /// Reconciles both directions. <paramref name="staleAfter"/> is how long a session may be live
    /// (or a claim held) before this steps in — it keeps the loop from racing the webhook.
    /// </summary>
    public async Task ReconcileAsync(TimeSpan staleAfter, CancellationToken ct = default)
    {
        IReadOnlySet<string> active;
        try
        {
            active = await _egress.GetActiveEgressIdsAsync(ct);
        }
        catch (Exception ex)
        {
            // Abandon the whole pass on purpose. Treating "LiveKit unreachable" as "nothing is
            // running" would start a duplicate recording for every session already in progress —
            // the worst possible response to a transient outage.
            _logger.LogWarning(ex, "Skipping reconciliation: could not read egress state.");
            return;
        }

        var cutoff = DateTime.UtcNow - staleAfter;
        await StartMissingRecordingsAsync(cutoff, ct);
        await StopOrphanedEgressesAsync(active, ct);
    }

    /// <summary>Direction 1: a session the teacher wants recorded that nothing is recording.</summary>
    private async Task StartMissingRecordingsAsync(DateTime cutoff, CancellationToken ct)
    {
        var pending = await _streams.GetLiveStreamsNeedingRecordingAsync(EgressClaim.Prefix, cutoff, ct);

        foreach (var stream in pending)
        {
            // Clear an abandoned placeholder first: the claim inside the starter requires NULL, so
            // a stale placeholder would otherwise block its own recovery forever.
            if (EgressClaim.IsClaim(stream.EgressId))
            {
                await _streams.SetEgressIdAsync(stream.Id, null, ct);
            }

            // Shared with the webhook and the teacher's toggle, so no two paths can both start a
            // composite for the same room.
            var outcome = await _recordingStarter.TryStartAsync(stream, ct);

            if (outcome == RecordingStartOutcome.Started)
            {
                _logger.LogWarning(
                    "Reconciliation started recording for session {SessionId} — nothing else had, "
                    + "though the teacher asked for it.",
                    stream.SessionId);
            }
        }
    }

    /// <summary>
    /// Direction 2: an egress running that should not be. Covers a session that has ended, and a
    /// teacher's stop whose request failed to reach LiveKit.
    /// </summary>
    private async Task StopOrphanedEgressesAsync(IReadOnlySet<string> active, CancellationToken ct)
    {
        foreach (var egressId in active)
        {
            var stream = await _streams.GetByEgressIdAsync(egressId, ct);

            // Not ours. Leave it alone — this service is not necessarily the only thing driving
            // LiveKit, and stopping a stranger's recording is far worse than leaking one.
            if (stream is null) continue;

            // Wanted and still live: exactly what should be running.
            if (stream.Status == StreamStatus.Live && stream.RecordingState == RecordingState.Recording)
            {
                continue;
            }

            try
            {
                await _egress.StopRecordingAsync(egressId, ct);
                _logger.LogWarning(
                    "Reconciliation stopped recording {EgressId} that should not be running: "
                    + "session {SessionId} is {Status}/{RecordingState}.",
                    egressId, stream.SessionId, stream.Status, stream.RecordingState);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "Reconciliation could not stop orphaned recording {EgressId}.", egressId);
            }
        }
    }
}
