namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Closes quizzes whose time has run out.
///
/// The deadline was already the authority on whether an ANSWER is accepted — a quiz past its time
/// refuses them regardless of status. What was missing is the other half: nothing moved the quiz to
/// Closed, so it sat Open indefinitely, and Closed is what releases marks to the class and hands
/// the composer back to the teacher. A teacher who forgot to press the button left the room with no
/// marks and no way to run another quiz.
///
/// Run on a short cadence by a hosted service. Deliberately an application-layer service rather
/// than logic inside the worker, so the rule is testable without a host.
/// </summary>
public interface IQuizDeadlineSweeper
{
    /// <summary>Closes every quiz past its deadline. Returns how many were closed.</summary>
    Task<int> SweepAsync(CancellationToken ct = default);
}
