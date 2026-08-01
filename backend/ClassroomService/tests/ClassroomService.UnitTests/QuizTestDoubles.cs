using ClassroomService.Application.Abstractions;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;

namespace ClassroomService.UnitTests;

/// <summary>
/// In-memory quiz store. Answers are held in a list rather than keyed, so a test can prove the
/// service UPDATES an existing answer instead of accumulating a second row — the behaviour the
/// unique index enforces in the real database.
/// </summary>
public sealed class FakeQuizRepository : IQuizRepository
{
    private readonly Dictionary<Guid, Quiz> _quizzes = new();
    public List<QuizAnswer> Answers { get; } = new();
    public List<QuizSubmission> Submissions { get; } = new();

    public void Seed(Quiz quiz) => _quizzes[quiz.Id] = quiz;
    public Quiz? Find(Guid quizId) => _quizzes.GetValueOrDefault(quizId);

    /// <summary>Everything stored, so a test can assert that an operation persisted NOTHING.</summary>
    public IReadOnlyCollection<Quiz> All => _quizzes.Values;

    public Task<Quiz?> GetWithQuestionsAsync(Guid quizId, CancellationToken ct = default)
        => Task.FromResult(_quizzes.GetValueOrDefault(quizId));

    public Task<Quiz?> GetOpenForSessionAsync(Guid sessionId, CancellationToken ct = default)
        => Task.FromResult(_quizzes.Values
            .FirstOrDefault(q => q.SessionId == sessionId && q.Status == QuizStatus.Open));

    public Task<List<Quiz>> GetOpenPastDeadlineAsync(
        DateTime cutoffUtc, CancellationToken ct = default)
        => Task.FromResult(_quizzes.Values
            .Where(q => q.Status == QuizStatus.Open
                        && q.ClosesAtUtc is not null
                        && q.ClosesAtUtc <= cutoffUtc)
            .OrderBy(q => q.ClosesAtUtc)
            .ToList());

    public Task AddAsync(Quiz quiz, CancellationToken ct = default)
    {
        _quizzes[quiz.Id] = quiz;
        return Task.CompletedTask;
    }

    public void RemoveQuestions(IEnumerable<QuizQuestion> questions) { /* cleared by the caller */ }

    public Task<List<Quiz>> GetForSessionAsync(Guid sessionId, CancellationToken ct = default)
        => Task.FromResult(_quizzes.Values
            .Where(q => q.SessionId == sessionId)
            .OrderBy(q => q.CreatedAtUtc)
            .ToList());

    public Task<List<QuizAnswer>> GetAnswersForSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var quizIds = _quizzes.Values.Where(q => q.SessionId == sessionId).Select(q => q.Id).ToHashSet();
        return Task.FromResult(Answers.Where(a => quizIds.Contains(a.QuizId)).ToList());
    }

    public Task<List<QuizSubmission>> GetSubmissionsForSessionAsync(
        Guid sessionId, CancellationToken ct = default)
    {
        var quizIds = _quizzes.Values.Where(q => q.SessionId == sessionId).Select(q => q.Id).ToHashSet();
        return Task.FromResult(Submissions.Where(s => quizIds.Contains(s.QuizId)).ToList());
    }

    public Task<List<QuizAnswer>> GetAnswersAsync(Guid quizId, CancellationToken ct = default)
        => Task.FromResult(Answers.Where(a => a.QuizId == quizId).ToList());

    public Task<List<QuizAnswer>> GetAnswersForStudentAsync(
        Guid quizId, Guid studentId, CancellationToken ct = default)
        => Task.FromResult(Answers.Where(a => a.QuizId == quizId && a.StudentId == studentId).ToList());

    public Task<QuizAnswer?> GetAnswerAsync(Guid questionId, Guid studentId, CancellationToken ct = default)
        => Task.FromResult(Answers.FirstOrDefault(a => a.QuestionId == questionId && a.StudentId == studentId));

    public Task AddAnswerAsync(QuizAnswer answer, CancellationToken ct = default)
    {
        Answers.Add(answer);
        return Task.CompletedTask;
    }

    public Task<QuizSubmission?> GetSubmissionAsync(
        Guid quizId, Guid studentId, CancellationToken ct = default)
        => Task.FromResult(Submissions
            .FirstOrDefault(s => s.QuizId == quizId && s.StudentId == studentId));

    public Task<int> CountSubmissionsAsync(Guid quizId, CancellationToken ct = default)
        => Task.FromResult(Submissions.Count(s => s.QuizId == quizId));

    public Task AddSubmissionAsync(QuizSubmission submission, CancellationToken ct = default)
    {
        Submissions.Add(submission);
        return Task.CompletedTask;
    }
}

/// <summary>Records broadcasts so tests can assert the room was told, and what it was told.</summary>
public sealed class RecordingQuizNotifier : IQuizNotifier
{
    public List<(Guid SessionId, Guid QuizId, string State)> Notifications { get; } = new();

    public Task QuizChangedAsync(Guid sessionId, Guid quizId, string state, CancellationToken ct = default)
    {
        Notifications.Add((sessionId, quizId, state));
        return Task.CompletedTask;
    }
}

public sealed class FakeQuizSettings : IQuizSettings
{
    public int MaxQuestionsPerQuiz { get; init; } = 20;
    public int MinAnswersPerQuestion { get; init; } = 2;
    public int MaxAnswersPerQuestion { get; init; } = 6;
    public int DefaultSecondsPerQuestion { get; init; } = 60;
    public int MaxQuizDurationSeconds { get; init; } = 7200;
    public int LateAnswerGraceSeconds { get; init; } = 3;
}
