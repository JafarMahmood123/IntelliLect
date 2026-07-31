namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Tells everyone in the live session that a quiz changed state.
///
/// ClassroomService owns the quiz; StreamingService owns the socket. This is the seam between
/// them. Deliberately carries only the id and the new state — never the quiz content — so each
/// client then fetches the view it is entitled to. That is what makes it structurally impossible
/// for the answer key to travel to a student over the broadcast.
///
/// Best-effort: a failed broadcast must not fail the teacher's action. The quiz is already stored,
/// and a client that missed the push still finds it on its next read.
/// </summary>
public interface IQuizNotifier
{
    Task QuizChangedAsync(Guid sessionId, Guid quizId, string state, CancellationToken ct = default);
}
