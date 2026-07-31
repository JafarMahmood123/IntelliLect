using ClassroomService.Application.Abstractions;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ClassroomService.Infrastructure.Persistence.Repositories;

public sealed class QuizRepository : IQuizRepository
{
    private readonly ApplicationDbContext _context;

    public QuizRepository(ApplicationDbContext context) => _context = context;

    public async Task<Quiz?> GetWithQuestionsAsync(Guid quizId, CancellationToken ct = default)
    {
        var quiz = await _context.Quizzes
            .Include(q => q.Questions)
            .ThenInclude(q => q.Options)
            .FirstOrDefaultAsync(q => q.Id == quizId, ct);

        return quiz is null ? null : Ordered(quiz);
    }

    public async Task<Quiz?> GetOpenForSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var quiz = await _context.Quizzes
            .Include(q => q.Questions)
            .ThenInclude(q => q.Options)
            .Where(q => q.SessionId == sessionId && q.Status == QuizStatus.Open)
            .OrderByDescending(q => q.PublishedAtUtc)
            .FirstOrDefaultAsync(ct);

        return quiz is null ? null : Ordered(quiz);
    }

    public async Task AddAsync(Quiz quiz, CancellationToken ct = default)
        => await _context.Quizzes.AddAsync(quiz, ct);

    public void RemoveQuestions(IEnumerable<QuizQuestion> questions)
        => _context.QuizQuestions.RemoveRange(questions);

    public async Task<List<Quiz>> GetForSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var quizzes = await _context.Quizzes
            .Include(q => q.Questions)
            .ThenInclude(q => q.Options)
            .Where(q => q.SessionId == sessionId)
            .OrderBy(q => q.CreatedAtUtc)
            .ToListAsync(ct);

        foreach (var quiz in quizzes) Ordered(quiz);
        return quizzes;
    }

    public async Task<List<QuizAnswer>> GetAnswersForSessionAsync(
        Guid sessionId, CancellationToken ct = default)
        => await _context.QuizAnswers
            .Where(a => _context.Quizzes.Any(q => q.Id == a.QuizId && q.SessionId == sessionId))
            .ToListAsync(ct);

    public async Task<List<QuizAnswer>> GetAnswersAsync(Guid quizId, CancellationToken ct = default)
        => await _context.QuizAnswers.Where(a => a.QuizId == quizId).ToListAsync(ct);

    public async Task<List<QuizAnswer>> GetAnswersForStudentAsync(
        Guid quizId, Guid studentId, CancellationToken ct = default)
        => await _context.QuizAnswers
            .Where(a => a.QuizId == quizId && a.StudentId == studentId)
            .ToListAsync(ct);

    public async Task<QuizAnswer?> GetAnswerAsync(
        Guid questionId, Guid studentId, CancellationToken ct = default)
        => await _context.QuizAnswers
            .FirstOrDefaultAsync(a => a.QuestionId == questionId && a.StudentId == studentId, ct);

    public async Task AddAnswerAsync(QuizAnswer answer, CancellationToken ct = default)
        => await _context.QuizAnswers.AddAsync(answer, ct);

    /// <summary>
    /// Sorts questions and options by their explicit Order in memory. EF cannot order an included
    /// collection portably, and relying on the order rows happen to come back in would shuffle a
    /// student's options between page loads.
    /// </summary>
    private static Quiz Ordered(Quiz quiz)
    {
        quiz.Questions = quiz.Questions.OrderBy(q => q.Order).ToList();
        foreach (var question in quiz.Questions)
        {
            question.Options = question.Options.OrderBy(o => o.Order).ToList();
        }
        return quiz;
    }
}
