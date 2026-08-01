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

    Task AddAsync(Quiz quiz, CancellationToken ct = default);

    /// <summary>Drops a draft's questions (and their options, by cascade) before rewriting them.</summary>
    void RemoveQuestions(IEnumerable<QuizQuestion> questions);

    Task<List<QuizAnswer>> GetAnswersAsync(Guid quizId, CancellationToken ct = default);

    /// <summary>Every quiz in a session with its questions and options, oldest first.</summary>
    Task<List<Quiz>> GetForSessionAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Every answer across a whole session, for the session-wide summaries.</summary>
    Task<List<QuizAnswer>> GetAnswersForSessionAsync(Guid sessionId, CancellationToken ct = default);

    Task<List<QuizAnswer>> GetAnswersForStudentAsync(Guid quizId, Guid studentId, CancellationToken ct = default);

    Task<QuizAnswer?> GetAnswerAsync(Guid questionId, Guid studentId, CancellationToken ct = default);

    Task AddAnswerAsync(QuizAnswer answer, CancellationToken ct = default);

    /// <summary>A student's submission for a quiz, or null if they have not finished it.</summary>
    Task<QuizSubmission?> GetSubmissionAsync(Guid quizId, Guid studentId, CancellationToken ct = default);

    /// <summary>How many students have declared themselves finished. Shown to the teacher, who
    /// can then close the quiz rather than waiting out a timer nobody is still using.</summary>
    Task<int> CountSubmissionsAsync(Guid quizId, CancellationToken ct = default);

    Task AddSubmissionAsync(QuizSubmission submission, CancellationToken ct = default);
}
