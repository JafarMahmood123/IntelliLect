namespace ClassroomService.Application.DTOs.Session;

/// <summary>Who asked for the session to end. Recorded in the logs for auditability.</summary>
public enum SessionEndTrigger
{
    /// <summary>The classroom's teacher pressed "End Session".</summary>
    Teacher,

    /// <summary>A super admin force-ended the session from the monitor.</summary>
    SuperAdmin,

    /// <summary>The periodic sweeper closed a session that had been live too long.</summary>
    StalledSweep
}

/// <summary>
/// Result of ending a session. The status/timestamp are authoritative (committed before the
/// downstream steps run); the two flags report the best-effort teardown so the caller can tell
/// the user when a session ended but, say, its summary could not be triggered.
/// </summary>
public sealed record SessionEndOutcome(
    Guid SessionId,
    string Status,
    bool AlreadyEnded,
    bool StreamEnded,
    bool SummaryTriggered,
    DateTime? EndedAtUtc);
