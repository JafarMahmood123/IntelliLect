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
        => await _context.Quizzes
            .Include(q => q.Questions.OrderBy(question => question.Order))
                .ThenInclude(question => question.Options.OrderBy(option => option.Order))
            .FirstOrDefaultAsync(q => q.Id == quizId, ct);

    public async Task<Quiz?> GetOpenForSessionAsync(Guid sessionId, CancellationToken ct = default)
        => await _context.Quizzes
            .Include(q => q.Questions.OrderBy(question => question.Order))
                .ThenInclude(question => question.Options.OrderBy(option => option.Order))
            .Where(q => q.SessionId == sessionId && q.Status == QuizStatus.Open)
            .OrderByDescending(q => q.PublishedAtUtc)
            .FirstOrDefaultAsync(ct);

    public async Task<List<Quiz>> GetOpenPastDeadlineAsync(
        DateTime cutoffUtc, CancellationToken ct = default)
        => await _context.Quizzes
            .Include(q => q.Questions.OrderBy(question => question.Order))
                .ThenInclude(question => question.Options.OrderBy(option => option.Order))
            .Where(q => q.Status == QuizStatus.Open
                        && q.ClosesAtUtc != null
                        && q.ClosesAtUtc <= cutoffUtc)
            .OrderBy(q => q.ClosesAtUtc)
            .ToListAsync(ct);

    public async Task AddAsync(Quiz quiz, CancellationToken ct = default)
        => await _context.Quizzes.AddAsync(quiz, ct);

    public void RemoveQuestions(IEnumerable<QuizQuestion> questions)
        => _context.QuizQuestions.RemoveRange(questions);

    public async Task<List<Quiz>> GetForSessionAsync(Guid sessionId, CancellationToken ct = default)
        => await _context.Quizzes
            .Include(q => q.Questions.OrderBy(question => question.Order))
                .ThenInclude(question => question.Options.OrderBy(option => option.Order))
            .Where(q => q.SessionId == sessionId)
            .OrderBy(q => q.CreatedAtUtc)
            .ToListAsync(ct);

    public async Task<List<QuizAnswer>> GetAnswersForSessionAsync(
        Guid sessionId, CancellationToken ct = default)
        => await _context.QuizAnswers
            .Where(a => _context.Quizzes.Any(q => q.Id == a.QuizId && q.SessionId == sessionId))
            .ToListAsync(ct);

    public async Task<List<QuizSubmission>> GetSubmissionsForSessionAsync(
        Guid sessionId, CancellationToken ct = default)
        => await _context.QuizSubmissions
            .Where(s => _context.Quizzes.Any(q => q.Id == s.QuizId && q.SessionId == sessionId))
            .ToListAsync(ct);

    public async Task<List<Quiz>> GetForClassroomAsync(
        Guid classroomId, CancellationToken ct = default)
        => await _context.Quizzes
            .Include(q => q.Questions.OrderBy(question => question.Order))
                .ThenInclude(question => question.Options.OrderBy(option => option.Order))
            .Where(q => q.ClassroomId == classroomId)
            .OrderBy(q => q.CreatedAtUtc)
            .ToListAsync(ct);

    public async Task<List<QuizAnswer>> GetAnswersForClassroomAsync(
        Guid classroomId, CancellationToken ct = default)
        => await _context.QuizAnswers
            .Where(a => _context.Quizzes.Any(q => q.Id == a.QuizId && q.ClassroomId == classroomId))
            .ToListAsync(ct);

    public async Task<List<QuizSubmission>> GetSubmissionsForClassroomAsync(
        Guid classroomId, CancellationToken ct = default)
        => await _context.QuizSubmissions
            .Where(s => _context.Quizzes.Any(q => q.Id == s.QuizId && q.ClassroomId == classroomId))
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

    public async Task<QuizSubmission?> GetSubmissionAsync(
        Guid quizId, Guid studentId, CancellationToken ct = default)
        => await _context.QuizSubmissions
            .FirstOrDefaultAsync(s => s.QuizId == quizId && s.StudentId == studentId, ct);

    public async Task<int> CountSubmissionsAsync(Guid quizId, CancellationToken ct = default)
        => await _context.QuizSubmissions.CountAsync(s => s.QuizId == quizId, ct);

    public async Task<List<QuizSubmission>> GetSubmissionsForQuizAsync(
        Guid quizId, CancellationToken ct = default)
        => await _context.QuizSubmissions.Where(s => s.QuizId == quizId).ToListAsync(ct);

    public async Task AddSubmissionAsync(QuizSubmission submission, CancellationToken ct = default)
        => await _context.QuizSubmissions.AddAsync(submission, ct);

    public async Task<QuizExtension?> GetExtensionAsync(
        Guid quizId, Guid studentId, CancellationToken ct = default)
        => await _context.QuizExtensions
            .FirstOrDefaultAsync(e => e.QuizId == quizId && e.StudentId == studentId, ct);

    public async Task<List<QuizExtension>> GetExtensionsAsync(
        Guid quizId, CancellationToken ct = default)
        => await _context.QuizExtensions.Where(e => e.QuizId == quizId).ToListAsync(ct);

    public async Task<List<QuizExtension>> GetExtensionsForQuizzesAsync(
        IReadOnlyCollection<Guid> quizIds, CancellationToken ct = default)
        => quizIds.Count == 0
            ? []
            : await _context.QuizExtensions.Where(e => quizIds.Contains(e.QuizId)).ToListAsync(ct);

    public async Task AddExtensionAsync(QuizExtension extension, CancellationToken ct = default)
        => await _context.QuizExtensions.AddAsync(extension, ct);

    // Ordering is done in the INCLUDE, not in memory afterwards.
    //
    // The previous version sorted by reassigning the navigations
    // (`quiz.Questions = quiz.Questions.OrderBy(...).ToList()`), which quietly swapped EF's own
    // tracked collection instances for plain lists and broke change tracking on the graph. Reads
    // did not care, so it looked harmless — until an update tried to replace a draft's questions
    // and EF issued deletes for rows it no longer had a correct picture of, failing the whole
    // request with a DbUpdateConcurrencyException.
    //
    // Ordered includes (EF Core 5+) sort in SQL and leave the tracked collections alone, which is
    // both the fix and the reason the ordering still cannot be left to whatever order rows come
    // back in — a student's options must not shuffle between page loads.
}
