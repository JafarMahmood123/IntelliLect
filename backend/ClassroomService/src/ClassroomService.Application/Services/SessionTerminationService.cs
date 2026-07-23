using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Session;
using ClassroomService.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace ClassroomService.Application.Services;

/// <summary>
/// Shared session teardown. The order matters and is the same for every trigger:
///   1. Claim Live -> Ended atomically and commit. The session is authoritatively over even if a
///      later step fails, and a concurrent caller (teacher vs. sweeper) loses the claim and
///      becomes a no-op instead of running the teardown twice.
///   2. End the stream — this is what actually evicts the students: StreamingService stops the
///      recording egress, tells the AI assistant to flush and tear down, closes the LiveKit room
///      and broadcasts the status change to the browsers.
///   3. Trigger summarization. It runs AFTER (2) on purpose: the summary is generated from the
///      transcript the assistant flushes on teardown.
/// Steps 2 and 3 are best-effort and independent — one failing never blocks the other.
/// </summary>
public sealed class SessionTerminationService : ISessionTerminationService
{
    private readonly ISessionRepository _sessionRepository;
    private readonly IStreamingInternalClient _streamingClient;
    private readonly IKnowledgeInternalClient _knowledgeClient;
    private readonly IClock _clock;
    private readonly ILogger<SessionTerminationService> _logger;

    public SessionTerminationService(
        ISessionRepository sessionRepository,
        IStreamingInternalClient streamingClient,
        IKnowledgeInternalClient knowledgeClient,
        IClock clock,
        ILogger<SessionTerminationService> logger)
    {
        _sessionRepository = sessionRepository;
        _streamingClient = streamingClient;
        _knowledgeClient = knowledgeClient;
        _clock = clock;
        _logger = logger;
    }

    public async Task<SessionEndOutcome> EndAsync(
        Guid sessionId, SessionEndTrigger trigger, string reason, CancellationToken ct = default)
    {
        var session = await _sessionRepository.GetByIdAsync(sessionId, ct);
        if (session is null)
        {
            throw new KeyNotFoundException("Session not found.");
        }

        // Anything that is not Live is a no-op: an already-ended session, one that never started,
        // or one being deleted. Nothing is changed and the caller is told why.
        if (session.Status != SessionStatus.Live)
        {
            return NoOp(session);
        }

        var endedAtUtc = _clock.UtcNow;

        // Atomic claim. A false here means someone else won the race between the read above and
        // this update — they are running the teardown, so we must not run it a second time.
        var claimed = await _sessionRepository.TryMarkEndedAsync(sessionId, endedAtUtc, ct);
        if (!claimed)
        {
            var current = await _sessionRepository.GetByIdAsync(sessionId, ct);
            _logger.LogInformation(
                "Session {SessionId} end requested by {Trigger} lost the race; another caller is ending it.",
                sessionId, trigger);
            return current is null ? NoOp(session) : NoOp(current);
        }

        // Keep the in-memory entity consistent with what was just written, so callers holding it
        // (and any tracked-entity reuse in the same scope) see the new state.
        session.Status = SessionStatus.Ended;
        session.EndedAtUtc = endedAtUtc;

        _logger.LogInformation(
            "Session {SessionId} in classroom {ClassroomId} ended by {Trigger} at {EndedAtUtc:o}. Reason: {Reason}",
            sessionId, session.ClassroomId, trigger, endedAtUtc,
            string.IsNullOrWhiteSpace(reason) ? "(none given)" : reason);

        var streamEnded = await _streamingClient.EndStreamAsync(sessionId, ct);
        if (!streamEnded)
        {
            _logger.LogWarning(
                "Session {SessionId} was ended but StreamingService teardown failed; participants may need to leave manually.",
                sessionId);
        }

        var summaryTriggered = await _knowledgeClient.TriggerSummaryAsync(sessionId, session.ClassroomId, ct);
        if (!summaryTriggered)
        {
            _logger.LogWarning(
                "Session {SessionId} was ended but summary generation could not be triggered.",
                sessionId);
        }

        return new SessionEndOutcome(
            sessionId,
            SessionStatus.Ended.ToString(),
            AlreadyEnded: false,
            streamEnded,
            summaryTriggered,
            endedAtUtc);
    }

    private static SessionEndOutcome NoOp(Session session) => new(
        session.Id,
        session.Status.ToString(),
        AlreadyEnded: session.Status == SessionStatus.Ended,
        StreamEnded: false,
        SummaryTriggered: false,
        session.EndedAtUtc);
}
