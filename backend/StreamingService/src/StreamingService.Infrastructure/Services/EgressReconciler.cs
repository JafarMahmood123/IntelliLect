using Microsoft.Extensions.Logging;
using StreamingService.Application.Abstractions;
using StreamingService.Domain.Entities;

namespace StreamingService.Infrastructure.Services;

/// <summary>
/// One reconciliation pass over recording state. Split from the hosted service so the logic is
/// testable without a timer, mirroring how ClassroomService keeps recording maintenance in a
/// service and lets the hosted service do nothing but schedule it.
///
/// Recording is otherwise started by exactly one unretried fire-and-forget webhook
/// (<c>room_started</c>): if this service is restarting, briefly unreachable, or throws when it
/// arrives, that lecture records nothing and leaves no trace. Since this stack is rebuilt
/// constantly, that window is hit often.
/// </summary>
public sealed class EgressReconciler
{
    private readonly IStreamRepository _streams;
    private readonly IRecordingEgressService _egress;
    private readonly ILogger<EgressReconciler> _logger;

    public EgressReconciler(
        IStreamRepository streams,
        IRecordingEgressService egress,
        ILogger<EgressReconciler> logger)
    {
        _streams = streams;
        _egress = egress;
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

    /// <summary>Direction 1: a live lecture that nothing is recording.</summary>
    private async Task StartMissingRecordingsAsync(DateTime cutoff, CancellationToken ct)
    {
        var pending = await _streams.GetLiveStreamsNeedingRecordingAsync(EgressClaim.Prefix, cutoff, ct);

        foreach (var stream in pending)
        {
            // Clear an abandoned placeholder first: the claim below requires NULL, so a stale
            // placeholder would otherwise block its own recovery forever.
            if (EgressClaim.IsClaim(stream.EgressId))
            {
                await _streams.SetEgressIdAsync(stream.Id, null, ct);
            }

            // Same claim the webhook takes, so the two paths can never both start a composite.
            if (!await _streams.TryClaimEgressSlotAsync(
                    stream.SessionId, EgressClaim.New(DateTime.UtcNow), ct))
            {
                continue;
            }

            await StartOneAsync(stream, ct);
        }
    }

    private async Task StartOneAsync(LiveStream stream, CancellationToken ct)
    {
        try
        {
            // Room name is the session id (LiveKitMediaProvider convention).
            var egressId = await _egress.StartRoomRecordingAsync(stream.SessionId.ToString(), ct);

            // Null means recording is disabled — release rather than hold a placeholder forever.
            await _streams.SetEgressIdAsync(
                stream.Id, string.IsNullOrWhiteSpace(egressId) ? null : egressId, ct);

            if (!string.IsNullOrWhiteSpace(egressId))
            {
                _logger.LogWarning(
                    "Reconciliation started recording {EgressId} for live session {SessionId} — its "
                    + "room_started webhook never produced one.",
                    egressId, stream.SessionId);
            }
        }
        catch (Exception ex)
        {
            await ReleaseClaimAsync(stream.Id, ct);
            // Expected while the room does not exist yet; the next pass retries.
            _logger.LogWarning(
                ex, "Reconciliation could not start recording for session {SessionId}.", stream.SessionId);
        }
    }

    /// <summary>Direction 2: an egress still running for a session that has ended.</summary>
    private async Task StopOrphanedEgressesAsync(IReadOnlySet<string> active, CancellationToken ct)
    {
        foreach (var egressId in active)
        {
            var stream = await _streams.GetByEgressIdAsync(egressId, ct);

            // Not ours. Leave it alone — this service is not necessarily the only thing driving
            // LiveKit, and stopping a stranger's recording is far worse than leaking one.
            if (stream is null || stream.Status == StreamStatus.Live) continue;

            try
            {
                await _egress.StopRecordingAsync(egressId, ct);
                _logger.LogWarning(
                    "Reconciliation stopped orphaned recording {EgressId}: session {SessionId} is {Status}.",
                    egressId, stream.SessionId, stream.Status);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "Reconciliation could not stop orphaned recording {EgressId}.", egressId);
            }
        }
    }

    private async Task ReleaseClaimAsync(Guid streamId, CancellationToken ct)
    {
        try
        {
            await _streams.SetEgressIdAsync(streamId, null, ct);
        }
        catch (Exception ex)
        {
            // Not fatal: the claim carries its own timestamp, so it ages out on a later pass.
            _logger.LogWarning(ex, "Could not release recording claim for stream {StreamId}.", streamId);
        }
    }
}
