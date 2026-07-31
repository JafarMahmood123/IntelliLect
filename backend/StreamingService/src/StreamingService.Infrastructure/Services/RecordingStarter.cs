using Microsoft.Extensions.Logging;
using StreamingService.Application.Abstractions;
using StreamingService.Domain.Entities;

namespace StreamingService.Infrastructure.Services;

/// <inheritdoc cref="IRecordingStarter"/>
public sealed class RecordingStarter : IRecordingStarter
{
    private readonly IStreamRepository _streams;
    private readonly IRecordingEgressService _egress;
    private readonly ILogger<RecordingStarter> _logger;

    public RecordingStarter(
        IStreamRepository streams,
        IRecordingEgressService egress,
        ILogger<RecordingStarter> logger)
    {
        _streams = streams;
        _egress = egress;
        _logger = logger;
    }

    public async Task<RecordingStartOutcome> TryStartAsync(LiveStream stream, CancellationToken ct = default)
    {
        // Reserve the slot in the DATABASE before calling LiveKit. A read-then-write guard cannot
        // arbitrate concurrent callers — they would both observe a free slot and both start.
        if (!await _streams.TryClaimEgressSlotAsync(
                stream.SessionId, EgressClaim.New(DateTime.UtcNow), ct))
        {
            _logger.LogInformation(
                "Another caller holds the recording claim for session {SessionId}; not starting a second egress.",
                stream.SessionId);
            return RecordingStartOutcome.ClaimLost;
        }

        try
        {
            // Room name is the session id (LiveKitMediaProvider convention).
            var egressId = await _egress.StartRoomRecordingAsync(stream.SessionId.ToString(), ct);

            if (string.IsNullOrWhiteSpace(egressId))
            {
                // Recording disabled — release rather than leave a placeholder held forever.
                await _streams.SetEgressIdAsync(stream.Id, null, ct);
                return RecordingStartOutcome.Disabled;
            }

            await _streams.SetEgressIdAsync(stream.Id, egressId, ct);

            _logger.LogInformation(
                "Recording egress {EgressId} started for session {SessionId}.", egressId, stream.SessionId);
            return RecordingStartOutcome.Started;
        }
        catch (Exception ex)
        {
            // Release so the next reconcile pass retries promptly instead of waiting for the
            // placeholder to age out.
            await TryReleaseClaimAsync(stream.Id, stream.SessionId, ct);

            _logger.LogWarning(
                ex, "Could not start recording for session {SessionId}.", stream.SessionId);
            return RecordingStartOutcome.Failed;
        }
    }

    private async Task TryReleaseClaimAsync(Guid streamId, Guid sessionId, CancellationToken ct)
    {
        try
        {
            await _streams.SetEgressIdAsync(streamId, null, ct);
        }
        catch (Exception ex)
        {
            // Not fatal: the claim carries its own timestamp, so it ages out on a later pass.
            _logger.LogWarning(
                ex,
                "Could not release the recording claim for session {SessionId}; it will age out.",
                sessionId);
        }
    }
}
