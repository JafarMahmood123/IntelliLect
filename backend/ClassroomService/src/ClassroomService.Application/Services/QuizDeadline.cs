namespace ClassroomService.Application.Services;

/// <summary>
/// When a quiz is over. One definition, because there are two callers and they must agree (§11.7).
///
/// <see cref="QuizService"/> refuses answers past the deadline; <see cref="QuizDeadlineSweeper"/>
/// closes quizzes past the deadline. They were separate expressions of the same rule, spelled
/// differently — <c>now &gt; closesAt + grace</c> against a cutoff compared with <c>&lt;=</c> —
/// and so they disagreed at exactly one instant: at <c>closesAt + grace</c> the sweep closed a
/// quiz whose answers the service was still accepting. A student answering in that instant would
/// be told their answer was recorded by a quiz that had just been closed underneath them.
///
/// One tick is not much of a bug. Two spellings of one rule is, because the next change to the
/// grace period only has to be applied to one of them.
/// </summary>
public static class QuizDeadline
{
    /// <summary>
    /// Whether <paramref name="deadline"/> has passed, allowing for the late-answer grace.
    ///
    /// The grace is not generosity: a click at T-0.5s can land at T+0.3s, and rejecting it would
    /// punish network latency rather than lateness. A null deadline is a quiz with no timer, which
    /// is never past.
    /// </summary>
    public static bool IsPast(DateTime? deadline, DateTime nowUtc, int graceSeconds)
        => deadline is { } closesAt
           && nowUtc > closesAt.AddSeconds(Math.Max(0, graceSeconds));
}
