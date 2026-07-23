using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Session;
using Microsoft.Extensions.Logging;

namespace ClassroomService.Application.Services;

/// <summary>
/// Finds sessions that have been Live past the stall threshold and closes each one through the
/// shared termination path, so a forgotten session is torn down exactly like a teacher-ended one
/// (students removed, recording finalized, summary triggered).
/// A session that fails to end is logged and skipped — the rest of the batch still runs, and the
/// next pass retries it because it is still Live.
/// </summary>
public sealed class StalledSessionSweeper : IStalledSessionSweeper
{
    private const int MinStalledAfterHours = 1;
    private const int DefaultBatchSize = 50;

    private readonly ISessionRepository _sessionRepository;
    private readonly ISessionTerminationService _termination;
    private readonly IStalledSessionSettings _settings;
    private readonly IClock _clock;
    private readonly ILogger<StalledSessionSweeper> _logger;

    public StalledSessionSweeper(
        ISessionRepository sessionRepository,
        ISessionTerminationService termination,
        IStalledSessionSettings settings,
        IClock clock,
        ILogger<StalledSessionSweeper> logger)
    {
        _sessionRepository = sessionRepository;
        _termination = termination;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var thresholdHours = Math.Max(MinStalledAfterHours, _settings.StalledAfterHours);
        var batchSize = _settings.StalledSweepBatchSize > 0 ? _settings.StalledSweepBatchSize : DefaultBatchSize;
        var cutoffUtc = _clock.UtcNow.AddHours(-thresholdHours);

        var stalledIds = await _sessionRepository.GetStalledLiveSessionIdsAsync(cutoffUtc, batchSize, ct);
        if (stalledIds.Count == 0)
        {
            _logger.LogDebug("Stalled-session sweep found nothing live since before {CutoffUtc:o}.", cutoffUtc);
            return 0;
        }

        _logger.LogInformation(
            "Stalled-session sweep found {Count} session(s) live since before {CutoffUtc:o} ({Hours}h threshold).",
            stalledIds.Count, cutoffUtc, thresholdHours);

        var reason = $"Automatically closed: still live more than {thresholdHours} hours after starting.";
        var closed = 0;

        foreach (var sessionId in stalledIds)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var outcome = await _termination.EndAsync(sessionId, SessionEndTrigger.StalledSweep, reason, ct);

                // AlreadyEnded here means a teacher closed it between the query and this call —
                // not a failure, just nothing left to do.
                if (!outcome.AlreadyEnded && outcome.EndedAtUtc is not null)
                {
                    closed++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The session stays Live, so the next pass picks it up again.
                _logger.LogError(ex, "Could not force-close stalled session {SessionId}; will retry next sweep.", sessionId);
            }
        }

        _logger.LogInformation("Stalled-session sweep closed {Closed} of {Found} session(s).", closed, stalledIds.Count);
        return closed;
    }
}
