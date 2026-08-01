using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Quiz;
using ClassroomService.Application.Exceptions;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace ClassroomService.Application.Services;

public sealed class QuizService : IQuizService
{
    /// <summary>
    /// Marks given to every generated question. One each keeps a generated quiz's arithmetic
    /// obvious, and the teacher reweights anything that deserves more before publishing.
    /// </summary>
    private const int DefaultGeneratedPoints = 1;

    private readonly IQuizRepository _quizRepository;
    private readonly IClassroomRepository _classroomRepository;
    private readonly IMembershipRepository _membershipRepository;
    private readonly ISessionRepository _sessionRepository;
    private readonly IQuizNotifier _notifier;
    private readonly ILiveAssistantInternalClient _liveAssistant;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IQuizSettings _settings;
    private readonly ILogger<QuizService> _logger;

    public QuizService(
        IQuizRepository quizRepository,
        IClassroomRepository classroomRepository,
        IMembershipRepository membershipRepository,
        ISessionRepository sessionRepository,
        IQuizNotifier notifier,
        ILiveAssistantInternalClient liveAssistant,
        IUnitOfWork unitOfWork,
        IClock clock,
        IQuizSettings settings,
        ILogger<QuizService> logger)
    {
        _quizRepository = quizRepository;
        _classroomRepository = classroomRepository;
        _membershipRepository = membershipRepository;
        _sessionRepository = sessionRepository;
        _notifier = notifier;
        _liveAssistant = liveAssistant;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _settings = settings;
        _logger = logger;
    }

    public QuizLimitsDto GetLimits() => new(
        _settings.MaxQuestionsPerQuiz,
        _settings.MinAnswersPerQuestion,
        _settings.MaxAnswersPerQuestion,
        _settings.DefaultSecondsPerQuestion,
        _settings.MaxQuizDurationSeconds);

    // --- teacher: compose -------------------------------------------------------

    /// <summary>
    /// Build a draft from what the teacher has just been explaining, via the assistant.
    ///
    /// The result is a DRAFT and nothing more: it lands in the same state, and goes through the
    /// same review, edit, publish and validation path, as a quiz composed by hand. That is the
    /// whole reason generation could be added without touching publishing, grading or the student
    /// projection — the model gets a proposal in front of the teacher, and the teacher still
    /// decides what the class sees.
    /// </summary>
    public async Task<QuizTeacherDto> GenerateDraftAsync(
        Guid classroomId, Guid sessionId, Guid teacherId, int questionCount, CancellationToken ct = default)
    {
        await EnsureGenerationAllowedAsync(classroomId, sessionId, teacherId, ct);

        // Clamped rather than rejected: asking for more questions than the limit allows is a
        // reasonable request to round down, not an error worth refusing a live teacher over.
        var count = Math.Clamp(questionCount, 1, _settings.MaxQuestionsPerQuiz);

        var generated = await _liveAssistant.GenerateQuizAsync(
            sessionId,
            classroomId,
            count,
            _settings.MinAnswersPerQuestion,
            _settings.MaxAnswersPerQuestion,
            // A whole quiz has nothing to avoid — it replaces the composer rather than adding to it.
            avoid: null,
            ct);

        // Marks and timing are this service's to set, not the model's: a mark is a pedagogical
        // weight the teacher owns, and the seconds default is already configured here.
        var draft = new QuizDraftRequest(
            generated.Title,
            generated.Questions
                .Select(q => new QuestionDraftRequest(
                    q.Text,
                    DefaultGeneratedPoints,
                    _settings.DefaultSecondsPerQuestion,
                    q.Options.Select(o => new OptionDraftRequest(o.Text, o.IsCorrect)).ToList()))
                .ToList());

        _logger.LogInformation(
            "Generated a {QuestionCount}-question quiz draft for session {SessionId} (grounded: {Grounded}).",
            draft.Questions.Count, sessionId, generated.Grounded);

        return await CreateDraftAsync(classroomId, sessionId, teacherId, draft, ct);
    }

    /// <summary>
    /// One generated question for the composer to append, from the same idea and material the
    /// whole-quiz generation uses.
    ///
    /// Nothing is persisted. The teacher is mid-compose and may delete it a second later; writing
    /// a quiz row per button press would leave the session full of abandoned drafts.
    /// </summary>
    public async Task<GeneratedQuestionDraftDto> GenerateQuestionAsync(
        Guid classroomId, Guid sessionId, Guid teacherId, IReadOnlyList<string>? avoid,
        CancellationToken ct = default)
    {
        await EnsureGenerationAllowedAsync(classroomId, sessionId, teacherId, ct);

        var generated = await _liveAssistant.GenerateQuizAsync(
            sessionId,
            classroomId,
            questionCount: 1,
            _settings.MinAnswersPerQuestion,
            _settings.MaxAnswersPerQuestion,
            avoid,
            ct);

        var question = generated.Questions.FirstOrDefault()
            ?? throw new ServiceUnavailableException(
                "The teaching assistant returned no question. Please try again.");

        return ToDraftDto(question);
    }

    /// <summary>
    /// Answers for a question the teacher wrote themselves. Also unpersisted, for the same reason.
    /// </summary>
    public async Task<GeneratedQuestionDraftDto> GenerateAnswersAsync(
        Guid classroomId, Guid sessionId, Guid teacherId, string questionText,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(questionText))
        {
            throw new ConflictException("Write the question first, then generate its answers.");
        }

        await EnsureGenerationAllowedAsync(classroomId, sessionId, teacherId, ct);

        var question = await _liveAssistant.GenerateAnswersAsync(
            sessionId,
            classroomId,
            questionText,
            _settings.MinAnswersPerQuestion,
            _settings.MaxAnswersPerQuestion,
            ct);

        return ToDraftDto(question);
    }

    /// <summary>
    /// Authorised BEFORE the assistant is called: generation costs a model call, and it should not
    /// be spendable by someone who could not use the result anyway.
    /// </summary>
    private async Task EnsureGenerationAllowedAsync(
        Guid classroomId, Guid sessionId, Guid teacherId, CancellationToken ct)
    {
        await EnsureTeacherAsync(classroomId, teacherId, ct);

        var session = await _sessionRepository.GetByIdAsync(sessionId, ct);
        if (session is null || session.ClassroomId != classroomId)
        {
            throw new KeyNotFoundException("Session not found.");
        }
    }

    private GeneratedQuestionDraftDto ToDraftDto(GeneratedQuestionDto question)
        => new(
            question.Text,
            DefaultGeneratedPoints,
            _settings.DefaultSecondsPerQuestion,
            question.Options.Select(o => new OptionDraftRequest(o.Text, o.IsCorrect)).ToList());

    public async Task<QuizTeacherDto> CreateDraftAsync(
        Guid classroomId, Guid sessionId, Guid teacherId, QuizDraftRequest request, CancellationToken ct = default)
    {
        await EnsureTeacherAsync(classroomId, teacherId, ct);

        var session = await _sessionRepository.GetByIdAsync(sessionId, ct);
        if (session is null || session.ClassroomId != classroomId)
        {
            throw new KeyNotFoundException("Session not found.");
        }

        var quiz = new Quiz
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            ClassroomId = classroomId,
            CreatedByTeacherId = teacherId,
            Title = request.Title ?? string.Empty,
            Status = QuizStatus.Draft,
            CreatedAtUtc = _clock.UtcNow,
        };

        ApplyDraft(quiz, request);
        await _quizRepository.AddAsync(quiz, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        return ToTeacherDto(quiz, answers: []);
    }

    public async Task<QuizTeacherDto> UpdateDraftAsync(
        Guid classroomId, Guid quizId, Guid teacherId, QuizDraftRequest request, CancellationToken ct = default)
    {
        var quiz = await ResolveForTeacherAsync(classroomId, quizId, teacherId, ct);

        // Editing a published quiz would silently invalidate answers already submitted against the
        // questions being replaced, so it is refused rather than merged.
        if (quiz.Status != QuizStatus.Draft)
        {
            throw new ConflictException("Only a draft quiz can be edited. This one has been published.");
        }

        quiz.Title = request.Title ?? string.Empty;
        _quizRepository.RemoveQuestions(quiz.Questions.ToList());
        quiz.Questions.Clear();
        ApplyDraft(quiz, request);

        await _unitOfWork.SaveChangesAsync(ct);
        return ToTeacherDto(quiz, answers: []);
    }

    // --- teacher: lifecycle -----------------------------------------------------

    public async Task<QuizTeacherDto> PublishAsync(
        Guid classroomId, Guid quizId, Guid teacherId, CancellationToken ct = default)
    {
        var quiz = await ResolveForTeacherAsync(classroomId, quizId, teacherId, ct);

        if (quiz.Status != QuizStatus.Draft)
        {
            throw new ConflictException("Only a draft quiz can be published.");
        }

        ValidateForPublish(quiz);

        var now = _clock.UtcNow;
        var totalSeconds = quiz.Questions.Sum(q => q.TimeLimitSeconds);

        quiz.Status = QuizStatus.Open;
        quiz.PublishedAtUtc = now;
        quiz.ClosesAtUtc = now.AddSeconds(totalSeconds);
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Quiz {QuizId} published to session {SessionId} with {QuestionCount} question(s), closing at {ClosesAtUtc:o}.",
            quiz.Id, quiz.SessionId, quiz.Questions.Count, quiz.ClosesAtUtc);

        await _notifier.QuizChangedAsync(quiz.SessionId, quiz.Id, quiz.Status.ToString(), ct);
        return ToTeacherDto(quiz, answers: []);
    }

    public async Task<QuizTeacherDto> CloseAsync(
        Guid classroomId, Guid quizId, Guid teacherId, CancellationToken ct = default)
    {
        var quiz = await ResolveForTeacherAsync(classroomId, quizId, teacherId, ct);

        if (quiz.Status != QuizStatus.Open)
        {
            throw new ConflictException("Only an open quiz can be closed.");
        }

        quiz.Status = QuizStatus.Closed;
        quiz.ClosedAtUtc = _clock.UtcNow;
        await _unitOfWork.SaveChangesAsync(ct);

        await _notifier.QuizChangedAsync(quiz.SessionId, quiz.Id, quiz.Status.ToString(), ct);
        return ToTeacherDto(quiz, await _quizRepository.GetAnswersAsync(quiz.Id, ct));
    }

    public async Task<QuizTeacherDto> CancelAsync(
        Guid classroomId, Guid quizId, Guid teacherId, CancellationToken ct = default)
    {
        var quiz = await ResolveForTeacherAsync(classroomId, quizId, teacherId, ct);

        if (quiz.Status == QuizStatus.Cancelled)
        {
            throw new ConflictException("This quiz has already been cancelled.");
        }

        // Reachable from Draft, Open and Closed alike. Answers are deliberately left in place:
        // cancelling mid-quiz must not throw away work everyone has already done, and a cancel made
        // by mistake has to be recoverable. They simply stop being counted (see CountsTowardsMarks).
        quiz.Status = QuizStatus.Cancelled;
        quiz.CancelledAtUtc = _clock.UtcNow;
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Quiz {QuizId} cancelled by teacher {TeacherId}; its answers are kept but no longer counted.",
            quiz.Id, teacherId);

        await _notifier.QuizChangedAsync(quiz.SessionId, quiz.Id, quiz.Status.ToString(), ct);
        return ToTeacherDto(quiz, await _quizRepository.GetAnswersAsync(quiz.Id, ct));
    }

    // --- reads ------------------------------------------------------------------

    public async Task<QuizTeacherDto> GetForTeacherAsync(
        Guid classroomId, Guid quizId, Guid teacherId, CancellationToken ct = default)
    {
        var quiz = await ResolveForTeacherAsync(classroomId, quizId, teacherId, ct);
        return ToTeacherDto(quiz, await _quizRepository.GetAnswersAsync(quiz.Id, ct));
    }

    public async Task<QuizStudentDto> GetForStudentAsync(
        Guid classroomId, Guid quizId, Guid userId, CancellationToken ct = default)
    {
        await EnsureMemberAsync(classroomId, userId, ct);

        var quiz = await _quizRepository.GetWithQuestionsAsync(quizId, ct);
        if (quiz is null || quiz.ClassroomId != classroomId)
        {
            throw new KeyNotFoundException("Quiz not found.");
        }

        // A draft has never been published; to a student it does not exist yet.
        if (quiz.Status == QuizStatus.Draft)
        {
            throw new KeyNotFoundException("Quiz not found.");
        }

        var mine = await _quizRepository.GetAnswersForStudentAsync(quiz.Id, userId, ct);
        return ToStudentDto(quiz, mine);
    }

    public async Task<QuizStudentDto?> GetOpenForSessionAsync(
        Guid classroomId, Guid sessionId, Guid userId, CancellationToken ct = default)
    {
        await EnsureMemberAsync(classroomId, userId, ct);

        var quiz = await _quizRepository.GetOpenForSessionAsync(sessionId, ct);
        if (quiz is null || quiz.ClassroomId != classroomId) return null;

        // Its deadline may have passed without anything having flipped the status yet; to a student
        // arriving now there is nothing to answer.
        if (IsPastDeadline(quiz)) return null;

        var mine = await _quizRepository.GetAnswersForStudentAsync(quiz.Id, userId, ct);
        return ToStudentDto(quiz, mine);
    }

    // --- student: answer --------------------------------------------------------

    public async Task<SubmitAnswerResultDto> SubmitAnswerAsync(
        Guid classroomId, Guid quizId, Guid studentId, string studentName,
        SubmitAnswerRequest request, CancellationToken ct = default)
    {
        await EnsureMemberAsync(classroomId, studentId, ct);

        var quiz = await _quizRepository.GetWithQuestionsAsync(quizId, ct);
        if (quiz is null || quiz.ClassroomId != classroomId)
        {
            throw new KeyNotFoundException("Quiz not found.");
        }

        if (quiz.Status != QuizStatus.Open)
        {
            throw new ConflictException("This quiz is not accepting answers.");
        }

        // The DEADLINE is the authority, not the status field. Nothing schedules a close, so a quiz
        // whose time has run out is still Open in the database — and must still refuse answers.
        if (IsPastDeadline(quiz))
        {
            throw new ConflictException("Time is up for this quiz.");
        }

        var question = quiz.Questions.FirstOrDefault(q => q.Id == request.QuestionId)
            ?? throw new KeyNotFoundException("Question not found.");

        var option = question.Options.FirstOrDefault(o => o.Id == request.OptionId)
            ?? throw new KeyNotFoundException("Answer option not found.");

        var isCorrect = option.IsCorrect;
        var points = isCorrect ? question.Points : 0;
        var now = _clock.UtcNow;

        // Changing an answer before the quiz closes updates the existing row. The unique index on
        // (QuestionId, StudentId) is what actually guarantees one answer per student — this read is
        // the ordinary path, not the safety mechanism.
        var existing = await _quizRepository.GetAnswerAsync(question.Id, studentId, ct);
        if (existing is not null)
        {
            existing.SelectedOptionId = option.Id;
            existing.StudentName = studentName;
            existing.IsCorrect = isCorrect;
            existing.PointsAwarded = points;
            existing.AnsweredAtUtc = now;
        }
        else
        {
            await _quizRepository.AddAnswerAsync(new QuizAnswer
            {
                Id = Guid.NewGuid(),
                QuizId = quiz.Id,
                QuestionId = question.Id,
                StudentId = studentId,
                StudentName = studentName,
                SelectedOptionId = option.Id,
                IsCorrect = isCorrect,
                PointsAwarded = points,
                AnsweredAtUtc = now,
            }, ct);
        }

        await _unitOfWork.SaveChangesAsync(ct);

        // Acknowledgement only — see SubmitAnswerResultDto for why correctness is withheld.
        return new SubmitAnswerResultDto(question.Id, option.Id, now);
    }

    // --- results ----------------------------------------------------------------

    public async Task<QuizResultsDto> GetResultsAsync(
        Guid classroomId, Guid quizId, Guid teacherId, CancellationToken ct = default)
    {
        var quiz = await ResolveForTeacherAsync(classroomId, quizId, teacherId, ct);
        var answers = await _quizRepository.GetAnswersAsync(quiz.Id, ct);
        var totalPoints = quiz.Questions.Sum(q => q.Points);

        var students = answers
            .GroupBy(a => a.StudentId)
            .Select(g => new StudentQuizResultDto(
                g.Key,
                g.Sum(a => a.PointsAwarded),
                totalPoints,
                g.Count(),
                g.Count(a => a.IsCorrect)))
            .OrderByDescending(s => s.Score)
            .ToList();

        return new QuizResultsDto(
            quiz.Id, quiz.Status.ToString(), totalPoints, CountsTowardsMarks(quiz), students);
    }

    public async Task<MyQuizResultDto> GetMyResultAsync(
        Guid classroomId, Guid quizId, Guid studentId, CancellationToken ct = default)
    {
        await EnsureMemberAsync(classroomId, studentId, ct);

        var quiz = await _quizRepository.GetWithQuestionsAsync(quizId, ct);
        if (quiz is null || quiz.ClassroomId != classroomId || quiz.Status == QuizStatus.Draft)
        {
            throw new KeyNotFoundException("Quiz not found.");
        }

        var mine = await _quizRepository.GetAnswersForStudentAsync(quiz.Id, studentId, ct);
        var totalPoints = quiz.Questions.Sum(q => q.Points);

        // Correctness is withheld while the quiz is still open, for the same reason the submit
        // acknowledgement withholds it: answers are still changeable.
        var revealed = quiz.Status is QuizStatus.Closed or QuizStatus.Cancelled;

        return new MyQuizResultDto(
            quiz.Id,
            quiz.Status.ToString(),
            revealed ? mine.Sum(a => a.PointsAwarded) : 0,
            totalPoints,
            CountsTowardsMarks(quiz),
            mine.Select(a => new MyAnswerDto(
                a.QuestionId,
                a.SelectedOptionId,
                revealed ? a.IsCorrect : null,
                revealed ? a.PointsAwarded : 0)).ToList());
    }


    // --- session-wide summaries ---------------------------------------------------

    public async Task<SessionQuizSummaryDto> GetSessionSummaryAsync(
        Guid classroomId, Guid sessionId, Guid teacherId, CancellationToken ct = default)
    {
        await EnsureTeacherAsync(classroomId, teacherId, ct);

        var quizzes = (await _quizRepository.GetForSessionAsync(sessionId, ct))
            .Where(q => q.ClassroomId == classroomId)
            .ToList();
        var answers = await _quizRepository.GetAnswersForSessionAsync(sessionId, ct);

        // Drafts were never shown to anyone, and cancelled quizzes are withdrawn from marks. Both
        // are excluded from what is AVAILABLE, so a percentage cannot be dragged down by a quiz
        // nobody sat or one the teacher pulled.
        var counted = quizzes.Where(q => q.Status is not QuizStatus.Draft && CountsTowardsMarks(q)).ToList();
        var countedIds = counted.Select(q => q.Id).ToHashSet();
        var totalAvailable = counted.Sum(q => q.Questions.Sum(x => x.Points));

        var students = answers
            .Where(a => countedIds.Contains(a.QuizId))
            .GroupBy(a => a.StudentId)
            .Select(g =>
            {
                var score = g.Sum(a => a.PointsAwarded);
                return new StudentScoreDto(
                    g.Key,
                    // Latest submission wins, so a renamed student shows their current name.
                    g.OrderByDescending(a => a.AnsweredAtUtc).First().StudentName,
                    score,
                    totalAvailable,
                    g.Count(),
                    g.Count(a => a.IsCorrect),
                    totalAvailable > 0 ? (int)Math.Round(score * 100.0 / totalAvailable) : 0);
            })
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.StudentName)
            .ToList();

        var byOption = answers
            .GroupBy(a => a.SelectedOptionId)
            .ToDictionary(g => g.Key, g => g.Count());

        var questions = quizzes
            .Where(q => q.Status != QuizStatus.Draft)
            .SelectMany(quiz => quiz.Questions.Select(question =>
            {
                var forQuestion = answers.Where(a => a.QuestionId == question.Id).ToList();
                return new QuestionBreakdownDto(
                    question.Id, quiz.Id, quiz.Title, quiz.Status.ToString(), CountsTowardsMarks(quiz),
                    question.Order, question.Text, question.Points,
                    forQuestion.Count, forQuestion.Count(a => a.IsCorrect),
                    question.Options
                        .OrderBy(o => o.Order)
                        .Select(o => new OptionBreakdownDto(
                            o.Id, o.Text, o.IsCorrect,
                            byOption.TryGetValue(o.Id, out var n) ? n : 0))
                        .ToList());
            }))
            .ToList();

        return new SessionQuizSummaryDto(
            sessionId, quizzes.Count(q => q.Status != QuizStatus.Draft), counted.Count,
            totalAvailable, students, questions);
    }

    public async Task<MySessionQuizSummaryDto> GetMySessionSummaryAsync(
        Guid classroomId, Guid sessionId, Guid studentId, CancellationToken ct = default)
    {
        await EnsureMemberAsync(classroomId, studentId, ct);

        var quizzes = (await _quizRepository.GetForSessionAsync(sessionId, ct))
            .Where(q => q.ClassroomId == classroomId && q.Status != QuizStatus.Draft)
            .ToList();
        var answers = (await _quizRepository.GetAnswersForSessionAsync(sessionId, ct))
            .Where(a => a.StudentId == studentId)
            .ToList();

        var rows = quizzes.Select(quiz =>
        {
            var mine = answers.Where(a => a.QuizId == quiz.Id).ToList();
            // Marks stay hidden while a quiz is open, for the same reason the submit acknowledgement
            // withholds correctness: the student can still change their answers.
            var revealed = quiz.Status is QuizStatus.Closed or QuizStatus.Cancelled;
            return new MyQuizScoreDto(
                quiz.Id, quiz.Title, quiz.Status.ToString(), CountsTowardsMarks(quiz),
                revealed ? mine.Sum(a => a.PointsAwarded) : 0,
                quiz.Questions.Sum(q => q.Points),
                mine.Count,
                quiz.Questions.Count);
        }).ToList();

        var counted = rows.Where(r => r.CountsTowardsMarks).ToList();
        var score = counted.Sum(r => r.Score);
        var available = counted.Sum(r => r.TotalPoints);

        return new MySessionQuizSummaryDto(
            sessionId, score, available,
            available > 0 ? (int)Math.Round(score * 100.0 / available) : 0,
            rows);
    }

    // --- helpers ----------------------------------------------------------------

    /// <summary>
    /// A cancelled quiz's marks are preserved but excluded from every total. Expressed once, here,
    /// so the rule cannot be applied in some queries and forgotten in others.
    /// </summary>
    private static bool CountsTowardsMarks(Quiz quiz) => quiz.Status != QuizStatus.Cancelled;

    private bool IsPastDeadline(Quiz quiz)
        => quiz.ClosesAtUtc is { } closesAt
           && _clock.UtcNow > closesAt.AddSeconds(Math.Max(0, _settings.LateAnswerGraceSeconds));

    private void ApplyDraft(Quiz quiz, QuizDraftRequest request)
    {
        var order = 0;
        foreach (var q in request.Questions ?? [])
        {
            var question = new QuizQuestion
            {
                Id = Guid.NewGuid(),
                QuizId = quiz.Id,
                Order = order++,
                Text = q.Text,
                Points = q.Points,
                TimeLimitSeconds = q.TimeLimitSeconds > 0
                    ? q.TimeLimitSeconds
                    : _settings.DefaultSecondsPerQuestion,
            };

            var optionOrder = 0;
            foreach (var o in q.Options ?? [])
            {
                question.Options.Add(new QuizAnswerOption
                {
                    Id = Guid.NewGuid(),
                    QuestionId = question.Id,
                    Order = optionOrder++,
                    Text = o.Text,
                    IsCorrect = o.IsCorrect,
                });
            }

            quiz.Questions.Add(question);
        }
    }

    /// <summary>
    /// Enforced at PUBLISH rather than on every draft keystroke, so a teacher can leave a quiz
    /// half-composed. These are the same limits the client is given, which is what stops the
    /// composer offering a value the server would reject.
    /// </summary>
    private void ValidateForPublish(Quiz quiz)
    {
        if (quiz.Questions.Count == 0)
        {
            throw new ConflictException("A quiz needs at least one question.");
        }

        if (quiz.Questions.Count > _settings.MaxQuestionsPerQuiz)
        {
            throw new ConflictException(
                $"A quiz can have at most {_settings.MaxQuestionsPerQuiz} questions.");
        }

        foreach (var question in quiz.Questions)
        {
            if (string.IsNullOrWhiteSpace(question.Text))
            {
                throw new ConflictException("Every question needs text.");
            }

            if (question.Options.Count < _settings.MinAnswersPerQuestion ||
                question.Options.Count > _settings.MaxAnswersPerQuestion)
            {
                throw new ConflictException(
                    $"Each question needs between {_settings.MinAnswersPerQuestion} and "
                    + $"{_settings.MaxAnswersPerQuestion} answers.");
            }

            // Exactly one — grading is an equality check, and zero or several correct options makes
            // the score either impossible or ambiguous.
            if (question.Options.Count(o => o.IsCorrect) != 1)
            {
                throw new ConflictException("Each question needs exactly one correct answer.");
            }

            if (question.Points <= 0)
            {
                throw new ConflictException("Each question needs a mark greater than zero.");
            }
        }

        var totalSeconds = quiz.Questions.Sum(q => q.TimeLimitSeconds);
        if (totalSeconds > _settings.MaxQuizDurationSeconds)
        {
            throw new ConflictException(
                $"The quiz is longer than the {_settings.MaxQuizDurationSeconds / 60}-minute maximum.");
        }
    }

    private QuizTeacherDto ToTeacherDto(Quiz quiz, IReadOnlyCollection<QuizAnswer> answers)
    {
        var perOption = answers
            .GroupBy(a => a.SelectedOptionId)
            .ToDictionary(g => g.Key, g => g.Count());

        return new QuizTeacherDto(
            quiz.Id,
            quiz.SessionId,
            quiz.Title,
            quiz.Status.ToString(),
            quiz.Questions.Sum(q => q.Points),
            quiz.Questions.Sum(q => q.TimeLimitSeconds),
            quiz.ClosesAtUtc,
            _clock.UtcNow,
            answers.Select(a => a.StudentId).Distinct().Count(),
            quiz.Questions
                .OrderBy(q => q.Order)
                .Select(q => new QuizQuestionTeacherDto(
                    q.Id, q.Order, q.Text, q.Points, q.TimeLimitSeconds,
                    q.Options.OrderBy(o => o.Order)
                        .Select(o => new QuizOptionTeacherDto(
                            o.Id, o.Order, o.Text, o.IsCorrect,
                            perOption.TryGetValue(o.Id, out var count) ? count : 0))
                        .ToList()))
                .ToList());
    }

    /// <summary>
    /// Projects to the student shape. The option type here has no IsCorrect member at all, so the
    /// answer key cannot reach a browser by omission — only by someone deliberately changing the
    /// contract.
    /// </summary>
    private QuizStudentDto ToStudentDto(Quiz quiz, IReadOnlyCollection<QuizAnswer> myAnswers)
    {
        var mine = myAnswers.ToDictionary(a => a.QuestionId, a => a.SelectedOptionId);

        return new QuizStudentDto(
            quiz.Id,
            quiz.SessionId,
            quiz.Title,
            quiz.Status.ToString(),
            quiz.Questions.Sum(q => q.Points),
            quiz.ClosesAtUtc,
            _clock.UtcNow,
            quiz.Questions
                .OrderBy(q => q.Order)
                .Select(q => new QuizQuestionStudentDto(
                    q.Id, q.Order, q.Text, q.Points, q.TimeLimitSeconds,
                    mine.TryGetValue(q.Id, out var selected) ? selected : null,
                    q.Options.OrderBy(o => o.Order)
                        .Select(o => new QuizOptionStudentDto(o.Id, o.Order, o.Text))
                        .ToList()))
                .ToList());
    }

    private async Task<Quiz> ResolveForTeacherAsync(
        Guid classroomId, Guid quizId, Guid teacherId, CancellationToken ct)
    {
        await EnsureTeacherAsync(classroomId, teacherId, ct);

        var quiz = await _quizRepository.GetWithQuestionsAsync(quizId, ct);
        // Unknown, or belongs to another classroom -> 404 either way, so nothing leaks across.
        if (quiz is null || quiz.ClassroomId != classroomId)
        {
            throw new KeyNotFoundException("Quiz not found.");
        }
        return quiz;
    }

    /// <summary>Only the classroom's own teacher. Another teacher holds a valid Teacher token.</summary>
    private async Task EnsureTeacherAsync(Guid classroomId, Guid teacherId, CancellationToken ct)
    {
        var classroom = await _classroomRepository.GetByIdAsync(classroomId, ct)
            ?? throw new KeyNotFoundException("Classroom not found.");

        if (classroom.TeacherId != teacherId)
        {
            throw new ForbiddenAccessException("Only the classroom's teacher can manage quizzes.");
        }
    }

    private async Task EnsureMemberAsync(Guid classroomId, Guid userId, CancellationToken ct)
    {
        var classroom = await _classroomRepository.GetByIdAsync(classroomId, ct)
            ?? throw new KeyNotFoundException("Classroom not found.");

        var isMember = classroom.TeacherId == userId
            || await _membershipRepository.IsEnrolledAsync(classroomId, userId, ct);

        if (!isMember)
        {
            throw new ForbiddenAccessException("You are not a member of this classroom.");
        }
    }
}
