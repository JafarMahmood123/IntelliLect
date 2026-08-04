using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Session;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
using IntelliLect.Contracts.Messages;
using Microsoft.Extensions.Logging;

namespace ClassroomService.Application.Services;

/// <summary>
/// Shared session teardown. The order matters and is the same for every trigger:
///   1. In ONE transaction: claim Live -> Ended, write the summary row as Generating, and stage
///      the summary request on the outbox. The session is authoritatively over even if a later
///      step fails, and a concurrent caller (teacher vs. sweeper) loses the claim and becomes a
///      no-op instead of running the teardown twice.
///   2. End the stream — this is what actually evicts the students: StreamingService stops the
///      recording egress, tells the AI assistant to flush and tear down, closes the LiveKit room
///      and broadcasts the status change to the browsers.
/// Step 2 is best-effort and never blocks the session from being ended.
/// </summary>
/// <remarks>
/// <para>
/// WHY THE SUMMARY REQUEST IS OUTBOXED RATHER THAN POSTED. It used to be a synchronous HTTP call
/// to RagService whose failure was logged and forgotten — so if that service was
/// unreachable at session end, the summary was simply never built and nothing anywhere recorded
/// that one was owed. The call was never a query (the caller only read the 202), so it belongs on
/// the bus.
/// </para>
/// <para>
/// WHY IT NEEDS AN EXPLICIT TRANSACTION. <c>TryMarkEndedAsync</c> uses <c>ExecuteUpdateAsync</c>,
/// which bypasses the change tracker and commits on its own, and nothing in this method called
/// <c>SaveChangesAsync</c>. MassTransit's <c>UseBusOutbox</c> only captures a published message
/// during <c>SaveChangesAsync</c>, so publishing here without a transaction would stage the
/// message and silently drop it. <c>ExecuteUpdateAsync</c> DOES join an ambient transaction, so
/// wrapping the three writes keeps the database as the race arbiter while making them atomic.
/// </para>
/// </remarks>
public sealed class SessionTerminationService : ISessionTerminationService
{
    private readonly ISessionRepository _sessionRepository;
    private readonly ISummaryRepository _summaryRepository;
    private readonly IStreamingInternalClient _streamingClient;
    private readonly IEventBus _eventBus;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly ILogger<SessionTerminationService> _logger;

    public SessionTerminationService(
        ISessionRepository sessionRepository,
        ISummaryRepository summaryRepository,
        IStreamingInternalClient streamingClient,
        IEventBus eventBus,
        IUnitOfWork unitOfWork,
        IClock clock,
        ILogger<SessionTerminationService> logger)
    {
        _sessionRepository = sessionRepository;
        _summaryRepository = summaryRepository;
        _streamingClient = streamingClient;
        _eventBus = eventBus;
        _unitOfWork = unitOfWork;
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

        bool summaryRequested;
        await _unitOfWork.BeginTransactionAsync(ct);
        try
        {
            // Atomic claim. A false here means someone else won the race between the read above
            // and this update — they are running the teardown, so we must not run it a second
            // time. ExecuteUpdateAsync joins this transaction, so the DB still arbitrates.
            var claimed = await _sessionRepository.TryMarkEndedAsync(sessionId, endedAtUtc, ct);
            if (!claimed)
            {
                await _unitOfWork.RollbackAsync(ct);
                var current = await _sessionRepository.GetByIdAsync(sessionId, ct);
                _logger.LogInformation(
                    "Session {SessionId} end requested by {Trigger} lost the race; another caller is ending it.",
                    sessionId, trigger);
                return current is null ? NoOp(session) : NoOp(current);
            }

            // Record that a summary is now owed. Until this existed, no row was ever written in
            // the Generating state — the summary sprang into existence already-terminal when the
            // consumer fired — so "never requested" and "in flight" were indistinguishable and a
            // regenerate request had nothing to reset.
            summaryRequested = await StageSummaryRequestAsync(session, ct);

            // Writes the OutboxMessage row for anything staged on IEventBus above.
            await _unitOfWork.SaveChangesAsync(ct);
            await _unitOfWork.CommitAsync(ct);
        }
        catch
        {
            await _unitOfWork.RollbackAsync(ct);
            throw;
        }

        // Keep the in-memory entity consistent with what was just written, so callers holding it
        // (and any tracked-entity reuse in the same scope) see the new state.
        session.Status = SessionStatus.Ended;
        session.EndedAtUtc = endedAtUtc;

        _logger.LogInformation(
            "Session {SessionId} in classroom {ClassroomId} ended by {Trigger} at {EndedAtUtc:o}. Reason: {Reason}",
            sessionId, session.ClassroomId, trigger, endedAtUtc,
            string.IsNullOrWhiteSpace(reason) ? "(none given)" : reason);

        // AFTER the commit on purpose: the summary is generated from the transcript the assistant
        // flushes on teardown, and the request is already durable, so a slow or failing stream
        // teardown can no longer cost us the summary.
        var streamEnded = await _streamingClient.EndStreamAsync(sessionId, ct);
        if (!streamEnded)
        {
            _logger.LogWarning(
                "Session {SessionId} was ended but StreamingService teardown failed; participants may need to leave manually.",
                sessionId);
        }

        var summaryTriggered = summaryRequested;

        return new SessionEndOutcome(
            sessionId,
            SessionStatus.Ended.ToString(),
            AlreadyEnded: false,
            streamEnded,
            summaryTriggered,
            endedAtUtc);
    }

    /// <summary>
    /// Marks the session's summary Generating and stages the request on the outbox. Both writes
    /// belong to the caller's transaction, so the request cannot survive a rolled-back session end
    /// (or be lost by a committed one).
    /// </summary>
    private async Task<bool> StageSummaryRequestAsync(Session session, CancellationToken ct)
    {
        var summary = await _summaryRepository.GetBySessionIdAsync(session.Id, ct);
        if (summary is null)
        {
            summary = new SessionSummary
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                ClassroomId = session.ClassroomId,
                Status = SummaryStatus.Generating,
                CreatedAtUtc = _clock.UtcNow,
            };
            await _summaryRepository.AddAsync(summary, ct);
        }
        else if (summary.Status == SummaryStatus.PendingDeletion)
        {
            // A super admin is removing this summary's files. Re-requesting would race the
            // deletion and could leave orphaned objects in S3.
            _logger.LogWarning(
                "Session {SessionId} ended while its summary is being deleted; not requesting a new one.",
                session.Id);
            return false;
        }
        else
        {
            summary.Status = SummaryStatus.Generating;
            summary.Error = null;
        }

        await _eventBus.PublishAsync(
            new SessionSummaryRequestedMessage(
                session.Id,
                session.ClassroomId,
                RequestedByUserId: null,
                Reason: SummaryRequestReasons.SessionEnded),
            ct);
        return true;
    }

    private static SessionEndOutcome NoOp(Session session) => new(
        session.Id,
        session.Status.ToString(),
        AlreadyEnded: session.Status == SessionStatus.Ended,
        StreamEnded: false,
        SummaryTriggered: false,
        session.EndedAtUtc);
}
