namespace IntelliLect.Contracts.Messages;

/// <summary>
/// Published by ClassroomService to ask RagService to build a session's summary. Consumed by
/// RagService's AMQP consumer, which claims the run and drives the pipeline.
/// </summary>
/// <remarks>
/// <para>
/// This replaces a direct HTTP POST to RagService at session end. The call was always a
/// NOTIFICATION rather than a query — the caller only ever read the 202 and logged it — and as a
/// synchronous hop it coupled session teardown to RagService being reachable: if the POST
/// failed, nothing recorded that a summary was owed and it was simply never built.
/// </para>
/// <para>
/// Published through <c>IEventBus</c> (an <c>IPublishEndpoint</c>, so it is captured by
/// <c>UseBusOutbox</c>) inside the same transaction that marks the session Ended and writes the
/// Generating row. Session-ended and summary-requested therefore commit together or not at all.
/// </para>
/// <para>
/// Delivery is at-least-once, and RagService retries internally, so the same session can
/// legitimately arrive more than once. The consumer deduplicates on an atomic claim keyed by
/// <see cref="SessionId"/> — without it a redelivery would pay for a second LLM run over the whole
/// lecture.
/// </para>
/// </remarks>
/// <param name="SessionId">The session to summarize. Also the dedup/claim key on the consumer side.</param>
/// <param name="ClassroomId">Owning classroom; used for retrieval grounding and the S3 key template.</param>
/// <param name="RequestedByUserId">Who asked, when a human did. Null for the automatic session-end request.</param>
/// <param name="Reason">
/// Diagnostics only — never branched on. One of <c>SessionEnded</c>, <c>ManualTeacher</c>,
/// <c>ManualSuperAdmin</c>; see <see cref="SummaryRequestReasons"/>.
/// </param>
public sealed record SessionSummaryRequestedMessage(
    Guid SessionId,
    Guid ClassroomId,
    Guid? RequestedByUserId = null,
    string Reason = SummaryRequestReasons.SessionEnded);

/// <summary>
/// Values for <see cref="SessionSummaryRequestedMessage.Reason"/>. Constants rather than an enum so
/// the wire format stays a plain string — the consumer is Python and reads it for logging only, and
/// an unrecognized value must never change behaviour on either side.
/// </summary>
public static class SummaryRequestReasons
{
    /// <summary>The automatic request issued when a session ends (teacher, super admin, or sweeper).</summary>
    public const string SessionEnded = "SessionEnded";

    /// <summary>A teacher re-requested a summary that had ended up Failed.</summary>
    public const string ManualTeacher = "ManualTeacher";

    /// <summary>A super admin forced a rebuild, which is allowed from any state but PendingDeletion.</summary>
    public const string ManualSuperAdmin = "ManualSuperAdmin";
}
