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

        var candidates = await _quizRepository.GetOpenPastDeadlineAsync(cutoff, ct);
        if (candidates.Count == 0)
        {
            return 0;
        }

        // A student given extra time is still answering after the class deadline. Closing on it
        // would cut them off AND hand them the answer key mid-question, since closing is what
        // releases the review. So the quiz's real end is the latest deadline anyone holds.
        var extensions = await _quizRepository.GetExtensionsForQuizzesAsync(
            candidates.Select(q => q.Id).ToList(), ct);
        var lastExtension = extensions
            .GroupBy(e => e.QuizId)
            .ToDictionary(g => g.Key, g => g.Max(e => e.ClosesAtUtc));

        var expired = candidates
            .Where(q => !lastExtension.TryGetValue(q.Id, out var until) || until < cutoff)
            .ToList();

        if (expired.Count == 0)
        {
            return 0;
        }

        // §11.7. Everything above is a DECISION taken from data read some milliseconds ago, and
        // between that read and the write below a teacher can grant more time — which is the one
        // thing a teacher does at exactly this moment, because the quiz is visibly running out.
        // The copy of the quiz held here still carries the deadline from before the extension, so
        // without this re-read the sweep closes a quiz that has just been extended: the class is
        // cut off mid-question AND handed the answer key, since closing is what releases the
        // review. Nothing would report it; the teacher would see a quiz they had just extended
        // sitting closed.
        //
        // This narrows the window from "the whole read phase, including a second round-trip for
        // extensions" to the save itself. It does not close it — that needs a concurrency token on
        // Quiz, which is recorded in the work plan as the remaining half, because making every
        // write to a quiz able to fail is a much larger change than making this one able to notice.
        var currentDeadlines = await _quizRepository.GetCurrentDeadlinesAsync(
            expired.Select(q => q.Id).ToList(), ct);

        // The SAME question the answer path asks, through the same helper — not a cutoff
        // comparison that happens to agree with it. See QuizDeadline.
        var reprieved = expired
            .Where(q => currentDeadlines.TryGetValue(q.Id, out var deadline)
                        && !QuizDeadline.IsPast(deadline, now, _settings.LateAnswerGraceSeconds))
            .ToList();

        if (reprieved.Count > 0)
        {
            foreach (var quiz in reprieved)
            {
                _logger.LogInformation(
                    "Quiz {QuizId} was given more time while the sweep was running; leaving it open.",
                    quiz.Id);
            }
            expired = expired.Except(reprieved).ToList();
            if (expired.Count == 0)
            {
                return 0;
            }
        }

        foreach (var quiz in expired)
        {
            quiz.Status = QuizStatus.Closed;
            // The last deadline anyone was working to, not the moment the sweep noticed. A quiz
            // that ran out at 10:05 was over at 10:05 whether the sweep ran a second later or the
            // service was restarting.
            quiz.ClosedAtUtc = lastExtension.TryGetValue(quiz.Id, out var extendedTo)
                && (quiz.ClosesAtUtc is null || extendedTo > quiz.ClosesAtUtc)
                ? extendedTo
                : quiz.ClosesAtUtc ?? now;
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
