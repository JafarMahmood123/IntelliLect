using ClassroomService.Application.Abstractions;
using ClassroomService.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace ClassroomService.Application.Services;

/// <summary>
/// Moves timed-out quizzes to Closed, which is what releases marks and frees the composer.
///
/// The close is exactly the one <c>QuizService.CloseAsync</c> performs — same status, same
/// timestamp, same broadcast — so a quiz that ran out of time is indistinguishable afterwards from
/// one the teacher closed by hand. Marking is not done here and never was: every answer was graded
/// and stored the moment the student picked it, so closing only decides when the class may SEE it.
/// </summary>
public sealed class QuizDeadlineSweeper : IQuizDeadlineSweeper
{
    private readonly IQuizRepository _quizRepository;
    private readonly IQuizNotifier _notifier;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IQuizSettings _settings;
    private readonly ILogger<QuizDeadlineSweeper> _logger;

    public QuizDeadlineSweeper(
        IQuizRepository quizRepository,
        IQuizNotifier notifier,
        IUnitOfWork unitOfWork,
        IClock clock,
        IQuizSettings settings,
        ILogger<QuizDeadlineSweeper> logger)
    {
        _quizRepository = quizRepository;
        _notifier = notifier;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _settings = settings;
        _logger = logger;
    }

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;

        // The SAME grace the answer path applies. Closing at the raw deadline would reject an
        // answer clicked at T-0.5s that lands at T+0.3s — punishing network latency, which is the
        // exact thing the grace exists to prevent.
        var cutoff = now.AddSeconds(-Math.Max(0, _settings.LateAnswerGraceSeconds));

        var expired = await _quizRepository.GetOpenPastDeadlineAsync(cutoff, ct);
        if (expired.Count == 0)
        {
            return 0;
        }

        foreach (var quiz in expired)
        {
            quiz.Status = QuizStatus.Closed;
            // Its deadline, not the moment the sweep noticed. A quiz that ran out at 10:05 was over
            // at 10:05 whether the sweep ran a second later or the service was restarting.
            quiz.ClosedAtUtc = quiz.ClosesAtUtc ?? now;
        }

        await _unitOfWork.SaveChangesAsync(ct);

        // Broadcast AFTER the save, one room at a time. A client told a quiz had closed would
        // otherwise be able to re-read it and still find it Open.
        foreach (var quiz in expired)
        {
            try
            {
                await _notifier.QuizChangedAsync(
                    quiz.SessionId, quiz.Id, quiz.Status.ToString(), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The close is already durable. A room that missed the nudge sees the change on its
                // next read, which is a far better outcome than abandoning the remaining rooms.
                _logger.LogWarning(
                    ex, "Could not announce the automatic close of quiz {QuizId}.", quiz.Id);
            }
        }

        _logger.LogInformation("Closed {Count} quiz(zes) whose time had run out.", expired.Count);
        return expired.Count;
    }
}
