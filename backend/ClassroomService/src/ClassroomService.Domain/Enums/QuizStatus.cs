namespace ClassroomService.Domain.Enums;

/// <summary>
/// Lifecycle of an in-session quiz. Explicit states rather than inference from timestamps: whether
/// a quiz still accepts answers is a rule the API has to enforce, and deriving it from a null check
/// somewhere is how a closed quiz quietly keeps scoring.
/// </summary>
public enum QuizStatus
{
    /// <summary>Teacher is composing it. Students cannot see it. The ONLY state it can be edited in.</summary>
    Draft = 0,

    /// <summary>Published and accepting answers until the deadline or the teacher closes it.</summary>
    Open = 1,

    /// <summary>Finished and graded. Terminal.</summary>
    Closed = 2,

    /// <summary>
    /// Withdrawn by the teacher and NOT counted towards any mark. Terminal, and reachable from any
    /// of the three states above.
    ///
    /// Cancelling deliberately preserves every answer row rather than deleting it: a teacher who
    /// cancels mid-quiz must not destroy the work of everyone who already answered, and a cancel
    /// made in error has to be recoverable. Exclusion happens when marks are summed, in one place,
    /// so it can never be half-applied.
    /// </summary>
    Cancelled = 3,
}
