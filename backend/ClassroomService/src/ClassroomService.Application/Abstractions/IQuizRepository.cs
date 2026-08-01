using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Abstractions;

public interface IQuizRepository
{
    /// <summary>The quiz with its questions and their options, ordered. Null when unknown.</summary>
    Task<Quiz?> GetWithQuestionsAsync(Guid quizId, CancellationToken ct = default);

    /// <summary>
    /// The session's currently-open quiz, if any. This is what a student who joins mid-quiz asks
    /// for — without it they would only ever learn about a quiz from the live broadcast they
    /// already missed.
    /// </summary>
    Task<Quiz?> GetOpenForSessionAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Open quizzes whose deadline passed at or before <paramref name="cutoffUtc"/>, with their
    /// questions.
    ///
    /// Nothing schedules a close, so without this a quiz whose time ran out stays Open forever: it
    /// refuses answers but never releases marks, and it keeps blocking the composer. The cutoff
    /// carries the late-answer grace, so an answer still in flight is never orphaned by the close
    /// that follows it.
    /// </summary>
    Task<List<Quiz>> GetOpenPastDeadlineAsync(DateTime cutoffUtc, CancellationToken ct = default);

    Task AddAsync(Quiz quiz, CancellationToken ct = default);

    /// <summary>Drops a draft's questions (and their options, by cascade) before rewriting them.</summary>
    void RemoveQuestions(IEnumerable<QuizQuestion> questions);

    Task<List<QuizAnswer>> GetAnswersAsync(Guid quizId, CancellationToken ct = default);

    /// <summary>Every quiz in a session with its questions and options, oldest first.</summary>
    Task<List<Quiz>> GetForSessionAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Every answer across a whole session, for the session-wide summaries.</summary>
    Task<List<QuizAnswer>> GetAnswersForSessionAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Every submission across a whole session. The summary needs these as well as the answers,
    /// because a student who finished without answering anything took part and would otherwise be
    /// missing from the class list entirely.
    /// </summary>
    Task<List<QuizSubmission>> GetSubmissionsForSessionAsync(Guid sessionId, CancellationToken ct = default);

    Task<List<QuizAnswer>> GetAnswersForStudentAsync(Guid quizId, Guid studentId, CancellationToken ct = default);

    Task<QuizAnswer?> GetAnswerAsync(Guid questionId, Guid studentId, CancellationToken ct = default);

    Task AddAnswerAsync(QuizAnswer answer, CancellationToken ct = default);

    /// <summary>A student's submission for a quiz, or null if they have not finished it.</summary>
    Task<QuizSubmission?> GetSubmissionAsync(Guid quizId, Guid studentId, CancellationToken ct = default);

    /// <summary>How many students have declared themselves finished. Shown to the teacher, who
    /// can then close the quiz rather than waiting out a timer nobody is still using.</summary>
    Task<int> CountSubmissionsAsync(Guid quizId, CancellationToken ct = default);

    /// <summary>Every submission on one quiz, for the teacher's list of who has finished.</summary>
    Task<List<QuizSubmission>> GetSubmissionsForQuizAsync(Guid quizId, CancellationToken ct = default);

    Task AddSubmissionAsync(QuizSubmission submission, CancellationToken ct = default);

    /// <summary>Extra time granted to one student on this quiz, or null if they have none.</summary>
    Task<QuizExtension?> GetExtensionAsync(Guid quizId, Guid studentId, CancellationToken ct = default);

    /// <summary>
    /// Every extension on a quiz. The deadline sweep needs these: closing at the class deadline
    /// while an extended student is still answering would both cut them off and reveal the answer
    /// key to them mid-quiz.
    /// </summary>
    Task<List<QuizExtension>> GetExtensionsAsync(Guid quizId, CancellationToken ct = default);

    /// <summary>Extensions across a whole session, so the sweep needs one query, not one per quiz.</summary>
    Task<List<QuizExtension>> GetExtensionsForQuizzesAsync(
        IReadOnlyCollection<Guid> quizIds, CancellationToken ct = default);

    Task AddExtensionAsync(QuizExtension extension, CancellationToken ct = default);
}
