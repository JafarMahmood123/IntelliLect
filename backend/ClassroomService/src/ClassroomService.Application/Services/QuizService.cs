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
    public async Task<GeneratedQuizDraftDto> GenerateDraftAsync(
        Guid classroomId, Guid sessionId, Guid teacherId, int questionCount,
        bool wholeSession = false, CancellationToken ct = default)
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
            wholeSession,
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
            "Generated a {QuestionCount}-question quiz draft for session {SessionId} "
            + "(wholeSession: {WholeSession}, grounded: {Grounded}, corrections: {Corrections}).",
            draft.Questions.Count, sessionId, wholeSession, generated.Grounded,
            generated.Corrections.Count);

        var quiz = await CreateDraftAsync(classroomId, sessionId, teacherId, draft, ct);
        return new GeneratedQuizDraftDto(quiz, ToCorrections(generated.Corrections));
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
            // Always the recent explanation: this appends ONE question to a draft the teacher is
            // composing about what they are teaching now, not about the lesson so far.
            wholeSession: false,
            ct);

        var question = generated.Questions.FirstOrDefault()
            ?? throw new ServiceUnavailableException(
                "The teaching assistant returned no question. Please try again.");

        // Corrections come back on the QUIZ for this route — it is the whole-quiz endpoint asked
        // for one question — so they are read from there rather than from the question.
        return ToDraftDto(question, generated.Corrections);
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

        return ToDraftDto(question, question.Corrections);
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

    private GeneratedQuestionDraftDto ToDraftDto(
        GeneratedQuestionDto question, IReadOnlyList<GeneratedCorrectionDto> corrections)
        => new(
            question.Text,
            DefaultGeneratedPoints,
            _settings.DefaultSecondsPerQuestion,
            question.Options.Select(o => new OptionDraftRequest(o.Text, o.IsCorrect)).ToList(),
            ToCorrections(corrections));

    private static List<QuizCorrectionDto> ToCorrections(
        IReadOnlyList<GeneratedCorrectionDto> corrections)
        => corrections.Select(c => new QuizCorrectionDto(c.Taught, c.Corrected)).ToList();

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
        return await BuildTeacherDtoAsync(quiz, ct);
    }

    public async Task<QuizTeacherDto> ExtendAsync(
        Guid classroomId, Guid quizId, Guid teacherId, ExtendQuizRequest request,
        CancellationToken ct = default)
    {
        var quiz = await ResolveForTeacherAsync(classroomId, quizId, teacherId, ct);

        // Only a running quiz. Extending a closed one would reopen it after its marks had been
        // released — students would already have seen the answer key.
        if (quiz.Status != QuizStatus.Open)
        {
            throw new ConflictException("Only a running quiz can be given more time.");
        }

        if (request.Seconds <= 0)
        {
            throw new ValidationException("Extra time must be more than zero seconds.");
        }

        if (request.Seconds > _settings.MaxQuizDurationSeconds)
        {
            throw new ValidationException(
                $"A quiz cannot be extended by more than {_settings.MaxQuizDurationSeconds} seconds at once.");
        }

        var studentIds = request.StudentIds?.Where(id => id != Guid.Empty).Distinct().ToList() ?? [];
        var now = _clock.UtcNow;

        if (studentIds.Count == 0)
        {
            // Everyone. Measured from whichever is LATER — the deadline or now — so extending a
            // quiz whose clock has already run out (the usual reason to) actually buys the class
            // the time asked for, rather than expiring again the moment it is granted.
            var basis = quiz.ClosesAtUtc is { } closesAt && closesAt > now ? closesAt : now;
            quiz.ClosesAtUtc = basis.AddSeconds(request.Seconds);
        }
        else
        {
            await ExtendForStudentsAsync(quiz, studentIds, request.Seconds, now, ct);
        }

        await _unitOfWork.SaveChangesAsync(ct);

        // Announced as a state change like any other, so every client re-reads and picks up its own
        // new deadline. The message carries no times: a student must be told THEIR deadline by the
        // endpoint that knows which student is asking.
        await _notifier.QuizChangedAsync(quiz.SessionId, quiz.Id, quiz.Status.ToString(), ct);

        _logger.LogInformation(
            "Quiz {QuizId} extended by {Seconds}s for {Scope}.",
            quiz.Id, request.Seconds,
            studentIds.Count == 0 ? "the whole class" : $"{studentIds.Count} student(s)");

        return await BuildTeacherDtoAsync(quiz, ct);
    }

    /// <summary>
    /// Grants extra time to named students, updating an existing grant rather than stacking a
    /// second row on it — the unique index would refuse that anyway, and "add five minutes" pressed
    /// twice should mean ten from now, not two overlapping windows.
    /// </summary>
    private async Task ExtendForStudentsAsync(
        Quiz quiz, List<Guid> studentIds, int seconds, DateTime now, CancellationToken ct)
    {
        var existing = (await _quizRepository.GetExtensionsAsync(quiz.Id, ct))
            .ToDictionary(e => e.StudentId);

        foreach (var studentId in studentIds)
        {
            // Their current deadline is whatever they already have, or the class's. Measured from
            // now when that has passed, for the same reason as the class-wide branch.
            var current = existing.TryGetValue(studentId, out var already)
                ? EffectiveDeadline(quiz, already)
                : quiz.ClosesAtUtc;
            var basis = current is { } deadline && deadline > now ? deadline : now;

            if (already is not null)
            {
                already.ClosesAtUtc = basis.AddSeconds(seconds);
                already.GrantedAtUtc = now;
                continue;
            }

            await _quizRepository.AddExtensionAsync(new QuizExtension
            {
                Id = Guid.NewGuid(),
                QuizId = quiz.Id,
                StudentId = studentId,
                ClosesAtUtc = basis.AddSeconds(seconds),
                GrantedAtUtc = now,
            }, ct);
        }
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
        return await BuildTeacherDtoAsync(quiz, ct);
    }

    // --- reads ------------------------------------------------------------------

    public async Task<QuizTeacherDto> GetForTeacherAsync(
        Guid classroomId, Guid quizId, Guid teacherId, CancellationToken ct = default)
    {
        var quiz = await ResolveForTeacherAsync(classroomId, quizId, teacherId, ct);
        return await BuildTeacherDtoAsync(quiz, ct);
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
        var submission = await _quizRepository.GetSubmissionAsync(quiz.Id, userId, ct);
        var extension = await _quizRepository.GetExtensionAsync(quiz.Id, userId, ct);
        return ToStudentDto(quiz, mine, submission?.SubmittedAtUtc, extension);
    }

    public async Task<QuizStudentDto?> GetOpenForSessionAsync(
        Guid classroomId, Guid sessionId, Guid userId, CancellationToken ct = default)
    {
        await EnsureMemberAsync(classroomId, userId, ct);

        var quiz = await _quizRepository.GetOpenForSessionAsync(sessionId, ct);
        if (quiz is null || quiz.ClassroomId != classroomId) return null;

        // Its deadline may have passed without the sweep having flipped the status yet; to a
        // student arriving now there is nothing to answer — unless they are one of the students
        // given extra time, for whom it is very much still running.
        var extension = await _quizRepository.GetExtensionAsync(quiz.Id, userId, ct);
        if (IsPastDeadline(quiz, extension)) return null;

        // Everything a student needs on rejoining is read from the server here, not replayed from
        // a broadcast they were not connected for: the quiz, their own answers so far, and whether
        // they had already declared themselves finished.
        var mine = await _quizRepository.GetAnswersForStudentAsync(quiz.Id, userId, ct);
        var submission = await _quizRepository.GetSubmissionAsync(quiz.Id, userId, ct);
        return ToStudentDto(quiz, mine, submission?.SubmittedAtUtc, extension);
    }

    // --- student: answer --------------------------------------------------------

    public async Task<SubmitAnswerResultDto> SubmitAnswerAsync(
        Guid classroomId, Guid quizId, Guid studentId, string studentName,
        SubmitAnswerRequest request, CancellationToken ct = default)
    {
        await EnsureEnrolledStudentAsync(classroomId, studentId, ct);

        var quiz = await _quizRepository.GetWithQuestionsAsync(quizId, ct);
        if (quiz is null || quiz.ClassroomId != classroomId)
        {
            throw new KeyNotFoundException("Quiz not found.");
        }

        if (quiz.Status != QuizStatus.Open)
        {
            throw new ConflictException("This quiz is not accepting answers.");
        }

        // THIS student's deadline, which may be later than the class's. The deadline is the
        // authority rather than the status field: the sweep closes a timed-out quiz within seconds,
        // but between the two it is still Open in the database and must already refuse answers.
        var extension = await _quizRepository.GetExtensionAsync(quiz.Id, studentId, ct);
        if (IsPastDeadline(quiz, extension))
        {
            throw new ConflictException("Time is up for this quiz.");
        }

        // Submitting is what freezes a student's answers. Until then they stay changeable, which is
        // the whole point of being able to think again before declaring yourself finished.
        var submission = await _quizRepository.GetSubmissionAsync(quiz.Id, studentId, ct);
        if (submission is not null)
        {
            throw new ConflictException("You have already submitted this quiz.");
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

    /// <summary>
    /// The student declares they have finished, without sitting out the rest of the timer. Their
    /// answers are frozen from this point; everything already answered still counts.
    ///
    /// IDEMPOTENT. A double-click, or a retry after a dropped response, must not tell a student
    /// something went wrong when their submission is safely recorded — so a repeat returns the
    /// submission that already exists rather than a conflict.
    /// </summary>
    public async Task<QuizSubmissionDto> SubmitQuizAsync(
        Guid classroomId, Guid quizId, Guid studentId, string studentName,
        CancellationToken ct = default)
    {
        await EnsureEnrolledStudentAsync(classroomId, studentId, ct);

        var quiz = await _quizRepository.GetWithQuestionsAsync(quizId, ct);
        if (quiz is null || quiz.ClassroomId != classroomId)
        {
            throw new KeyNotFoundException("Quiz not found.");
        }

        var existing = await _quizRepository.GetSubmissionAsync(quiz.Id, studentId, ct);
        if (existing is not null)
        {
            return await BuildSubmissionDtoAsync(quiz, existing.SubmittedAtUtc, studentId, ct);
        }

        if (quiz.Status != QuizStatus.Open)
        {
            throw new ConflictException("This quiz is not accepting submissions.");
        }

        // Past the deadline there is nothing left to freeze — the answers are already final — so
        // this is refused rather than recording a submission that changes nothing. Their own
        // deadline, so a student given extra time can still declare themselves finished during it.
        if (IsPastDeadline(quiz, await _quizRepository.GetExtensionAsync(quiz.Id, studentId, ct)))
        {
            throw new ConflictException("Time is up for this quiz.");
        }

        var now = _clock.UtcNow;
        await _quizRepository.AddSubmissionAsync(new QuizSubmission
        {
            Id = Guid.NewGuid(),
            QuizId = quiz.Id,
            StudentId = studentId,
            StudentName = studentName,
            SubmittedAtUtc = now,
        }, ct);

        await _unitOfWork.SaveChangesAsync(ct);

        // Tells the room a submission landed, so the teacher's "n submitted" count moves without
        // them refreshing. Best-effort, like every other quiz broadcast.
        await _notifier.QuizChangedAsync(quiz.SessionId, quiz.Id, quiz.Status.ToString(), ct);

        _logger.LogInformation("Student {StudentId} submitted quiz {QuizId}.", studentId, quiz.Id);

        return await BuildSubmissionDtoAsync(quiz, now, studentId, ct);
    }

    private async Task<QuizSubmissionDto> BuildSubmissionDtoAsync(
        Quiz quiz, DateTime submittedAtUtc, Guid studentId, CancellationToken ct)
    {
        var mine = await _quizRepository.GetAnswersForStudentAsync(quiz.Id, studentId, ct);

        // Marks are NOT revealed here. Freezing this student's answers does not close the quiz for
        // everyone else, and telling an early finisher which options were right hands them the
        // answer key while their classmates are still choosing.
        return new QuizSubmissionDto(
            quiz.Id,
            submittedAtUtc,
            mine.Count,
            quiz.Questions.Count);
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
        var submissions = await _quizRepository.GetSubmissionsForSessionAsync(sessionId, ct);

        // Drafts were never shown to anyone, and cancelled quizzes are withdrawn from marks. Both
        // are excluded from what is AVAILABLE, so a percentage cannot be dragged down by a quiz
        // nobody sat or one the teacher pulled.
        var counted = CountedQuizzes(quizzes);
        var countedIds = counted.Select(q => q.Id).ToHashSet();
        var totalAvailable = counted.Sum(q => q.Questions.Sum(x => x.Points));

        // Drafts are excluded from the record as well as the totals: a draft's questions were never
        // put to anyone, so there is nothing about them for a teacher to review.
        var publishedIds = quizzes.Where(q => q.Status != QuizStatus.Draft).Select(q => q.Id).ToHashSet();
        var visibleAnswers = answers.Where(a => publishedIds.Contains(a.QuizId)).ToList();

        // The class list is everyone who took part, from BOTH sources. Building it from answers
        // alone would silently drop a student who opened the quiz, answered nothing and finished —
        // which is precisely the student a teacher most wants to see in this table.
        var participants = visibleAnswers
            .Select(a => (a.StudentId, a.StudentName, At: a.AnsweredAtUtc))
            .Concat(submissions
                .Where(s => publishedIds.Contains(s.QuizId))
                .Select(s => (s.StudentId, s.StudentName, At: s.SubmittedAtUtc)))
            .GroupBy(x => x.StudentId)
            // Latest wins, so a renamed student shows their current name.
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.At).First().StudentName);

        var answersByStudent = visibleAnswers
            .GroupBy(a => a.StudentId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var students = participants
            .Select(participant =>
            {
                var mine = answersByStudent.GetValueOrDefault(participant.Key) ?? [];
                // Scored on counted quizzes only; LISTED in full. A cancelled quiz's answers are
                // still part of what this student did, they just stop being worth anything.
                var scoring = mine.Where(a => countedIds.Contains(a.QuizId)).ToList();
                var score = scoring.Sum(a => a.PointsAwarded);
                return new StudentScoreDto(
                    participant.Key,
                    participant.Value,
                    score,
                    totalAvailable,
                    scoring.Count,
                    scoring.Count(a => a.IsCorrect),
                    totalAvailable > 0 ? (int)Math.Round(score * 100.0 / totalAvailable) : 0,
                    mine
                        .OrderBy(a => a.AnsweredAtUtc)
                        .Select(a => new StudentAnswerDto(
                            a.QuizId, a.QuestionId, a.SelectedOptionId,
                            a.IsCorrect, a.PointsAwarded, a.AnsweredAtUtc))
                        .ToList());
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
                quiz.Questions.Count,
                // The same gate as the score, and for the same reason — the review names the correct
                // option, so releasing it early would end the quiz for anyone who reads it.
                revealed ? BuildReview(quiz, mine) : []);
        }).ToList();

        var counted = rows.Where(r => r.CountsTowardsMarks).ToList();
        var score = counted.Sum(r => r.Score);
        var available = counted.Sum(r => r.TotalPoints);

        return new MySessionQuizSummaryDto(
            sessionId, score, available,
            available > 0 ? (int)Math.Round(score * 100.0 / available) : 0,
            rows);
    }

    // --- classroom-wide tracking ----------------------------------------------------

    public async Task<ClassroomQuizTrackingDto> GetClassroomTrackingAsync(
        Guid classroomId, Guid teacherId, CancellationToken ct = default)
    {
        await EnsureTeacherAsync(classroomId, teacherId, ct);

        var (quizzes, answers, submissions, sessions) = await LoadClassroomAsync(classroomId, ct);
        var counted = CountedQuizzes(quizzes);
        var countedIds = counted.Select(q => q.Id).ToHashSet();
        var totalAvailable = counted.Sum(q => q.Questions.Sum(x => x.Points));
        var sessionOfQuiz = quizzes.ToDictionary(q => q.Id, q => q.SessionId);
        var sessionsWithQuizzes = counted.Select(q => q.SessionId).ToHashSet();

        var participants = Participants(answers, submissions, countedIds);

        var rows = participants
            .Select(participant =>
            {
                var mine = answers
                    .Where(a => a.StudentId == participant.Key && countedIds.Contains(a.QuizId))
                    .ToList();
                var mySubmissions = submissions
                    .Where(s => s.StudentId == participant.Key && countedIds.Contains(s.QuizId))
                    .ToList();
                var score = mine.Sum(a => a.PointsAwarded);

                // A quiz counts as "taken" if they answered it OR declared themselves finished on
                // it. Answers alone would miss the student who opened it and engaged with nothing —
                // which is a fact about their term worth keeping, not one to round away.
                var quizzesTaken = mine.Select(a => a.QuizId)
                    .Concat(mySubmissions.Select(s => s.QuizId))
                    .Distinct()
                    .Count();

                var sessionsTakenPart = mine.Select(a => a.QuizId)
                    .Concat(mySubmissions.Select(s => s.QuizId))
                    .Select(quizId => sessionOfQuiz.GetValueOrDefault(quizId))
                    .Distinct()
                    .Count();

                return new StudentTrackingDto(
                    participant.Key,
                    participant.Value,
                    // Filled in by Ranked below: a position is a property of the whole class, so
                    // it cannot be known while building one row of it.
                    0,
                    quizzesTaken,
                    counted.Count,
                    mine.Count,
                    mine.Count(a => a.IsCorrect),
                    score,
                    totalAvailable,
                    Percentage(score, totalAvailable),
                    sessionsTakenPart,
                    sessionsWithQuizzes.Count);
            });

        var students = Ranked(rows);

        var members = await _membershipRepository.GetMembersWithDetailsAsync(classroomId, ct);

        return new ClassroomQuizTrackingDto(
            classroomId,
            members.Count,
            students.Count,
            sessions.Count,
            sessionsWithQuizzes.Count,
            counted.Count,
            totalAvailable,
            // Mean of the students who took part, not of the enrolled list. Counting a student who
            // has never sat a quiz as a zero would say the class is failing when it is absent.
            students.Count == 0 ? 0 : (int)Math.Round(students.Average(s => s.Percentage)),
            students,
            BuildSessionTracking(sessions, counted, answers, submissions));
    }

    public async Task<MyClassroomQuizTrackingDto> GetMyClassroomTrackingAsync(
        Guid classroomId, Guid studentId, CancellationToken ct = default)
    {
        await EnsureMemberAsync(classroomId, studentId, ct);

        var (quizzes, answers, submissions, sessions) = await LoadClassroomAsync(classroomId, ct);

        // GRADED, not merely counted — see GradedQuizzes. A student's own totals must never move
        // while a quiz is still open.
        var graded = GradedQuizzes(quizzes);
        var gradedIds = graded.Select(q => q.Id).ToHashSet();
        var totalAvailable = graded.Sum(q => q.Questions.Sum(x => x.Points));

        var mine = answers
            .Where(a => a.StudentId == studentId && gradedIds.Contains(a.QuizId))
            .ToList();
        var mySubmissions = submissions
            .Where(s => s.StudentId == studentId && gradedIds.Contains(s.QuizId))
            .ToList();
        var myQuizIds = mine.Select(a => a.QuizId)
            .Concat(mySubmissions.Select(s => s.QuizId))
            .ToHashSet();
        var score = mine.Sum(a => a.PointsAwarded);

        var quizzesBySession = graded.GroupBy(q => q.SessionId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var rows = sessions
            .Where(session => quizzesBySession.ContainsKey(session.Id))
            .Select(session =>
            {
                var sessionQuizzes = quizzesBySession[session.Id];
                var sessionQuizIds = sessionQuizzes.Select(q => q.Id).ToHashSet();
                var sessionScore = mine
                    .Where(a => sessionQuizIds.Contains(a.QuizId))
                    .Sum(a => a.PointsAwarded);
                var sessionTotal = sessionQuizzes.Sum(q => q.Questions.Sum(x => x.Points));

                return new MySessionTrackingDto(
                    session.Id,
                    session.Title,
                    session.ScheduledAtUtc,
                    session.StartedAtUtc,
                    sessionScore,
                    sessionTotal,
                    Percentage(sessionScore, sessionTotal),
                    sessionQuizIds.Count(myQuizIds.Contains),
                    sessionQuizzes.Count);
            })
            .OrderByDescending(r => r.ScheduledAtUtc)
            .ToList();

        // The class average, computed the same way the teacher's view computes it, so the two
        // never disagree about the number a student is being compared against.
        var classAverage = ClassAverage(answers, submissions, gradedIds, totalAvailable);

        // Everyone's graded total, which is all a position needs. Built here rather than reusing
        // the teacher's ranking because that one is over COUNTED quizzes: a rank that moved the
        // moment you answered an open question would hand back the correctness this view exists
        // to withhold.
        var scoreByStudent = Participants(answers, submissions, gradedIds).Keys
            .ToDictionary(
                id => id,
                id => answers
                    .Where(a => a.StudentId == id && gradedIds.Contains(a.QuizId))
                    .Sum(a => a.PointsAwarded));

        return new MyClassroomQuizTrackingDto(
            classroomId,
            RankOf(studentId, scoreByStudent),
            scoreByStudent.Count,
            score,
            totalAvailable,
            Percentage(score, totalAvailable),
            myQuizIds.Count,
            graded.Count,
            rows.Count(r => r.QuizzesTaken > 0),
            rows.Count,
            classAverage,
            rows);
    }

    private async Task<(List<Quiz> Quizzes, List<QuizAnswer> Answers,
        List<QuizSubmission> Submissions, List<Session> Sessions)> LoadClassroomAsync(
        Guid classroomId, CancellationToken ct)
        => (await _quizRepository.GetForClassroomAsync(classroomId, ct),
            await _quizRepository.GetAnswersForClassroomAsync(classroomId, ct),
            await _quizRepository.GetSubmissionsForClassroomAsync(classroomId, ct),
            (await _sessionRepository.GetByClassroomIdAsync(classroomId, ct)).ToList());

    /// <summary>
    /// Quizzes that count towards a total: published, and not withdrawn. The same rule the session
    /// summary applies, expressed once so a term total and a lesson total cannot disagree.
    /// </summary>
    private static List<Quiz> CountedQuizzes(List<Quiz> quizzes)
        => quizzes.Where(q => q.Status is not QuizStatus.Draft && CountsTowardsMarks(q)).ToList();

    /// <summary>
    /// Quizzes a STUDENT may see their own marks for: counted, and finished.
    ///
    /// Narrower than <see cref="CountedQuizzes"/> by exactly the open ones, and that gap is the
    /// point. <c>PointsAwarded</c> is written when the answer is written, not when the quiz closes,
    /// so any student-facing total that includes an open quiz moves the moment they answer it —
    /// and a total that moves by the question's points, or doesn't, tells them whether they were
    /// right while their answer is still changeable. That is the same disclosure
    /// <see cref="GetMyResultAsync"/> and <c>SubmitAnswerResultDto</c> deliberately withhold, so
    /// leaving it reachable by arithmetic here would make their withholding decorative.
    ///
    /// A teacher's view keeps the open quizzes: watching a live quiz fill in is the whole job. The
    /// two views therefore disagree while a quiz is open, which is correct — they answer different
    /// questions ("what has been graded" vs "what is happening now").
    /// </summary>
    private static List<Quiz> GradedQuizzes(List<Quiz> quizzes)
        => quizzes.Where(q => q.Status == QuizStatus.Closed).ToList();

    private static int Percentage(int score, int available)
        => available > 0 ? (int)Math.Round(score * 100.0 / available) : 0;

    /// <summary>
    /// Orders students best-first and stamps each with a position.
    ///
    /// STANDARD COMPETITION RANKING: equal scores share a position, and the next distinct score
    /// skips the positions the tie consumed — two students on 18 marks are both 2nd, and the next
    /// is 4th. Ranking them 2nd and 3rd by name would tell the teacher that one beat the other
    /// when the marks say nothing of the kind, and a ranking must not invent a difference the
    /// scores do not contain.
    ///
    /// Name still orders the tied group, so the same data always returns the same list — a table
    /// that reshuffles on refresh reads as marks changing.
    ///
    /// Ranked on SCORE rather than percentage, which is the same ordering: every student is
    /// measured against the same class-wide total, so the percentage is a monotonic function of
    /// the score. Score is the one that cannot round two different students onto the same number.
    /// </summary>
    private static List<StudentTrackingDto> Ranked(IEnumerable<StudentTrackingDto> students)
    {
        var ordered = students
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.StudentName)
            .ToList();

        var ranked = new List<StudentTrackingDto>(ordered.Count);
        var rank = 0;
        int? previousScore = null;

        for (var index = 0; index < ordered.Count; index++)
        {
            if (previousScore != ordered[index].Score)
            {
                // The position is the row number, not a running counter — which is exactly what
                // makes the tied positions get skipped.
                rank = index + 1;
                previousScore = ordered[index].Score;
            }

            ranked.Add(ordered[index] with { Rank = rank });
        }

        return ranked;
    }

    /// <summary>
    /// One student's position among their classmates, or null when they have taken nothing.
    ///
    /// The same competition rule as <see cref="Ranked"/>, stated the other way round: a position
    /// is one more than the number of students who genuinely beat you. Everyone level with you is
    /// on your position, not above it.
    /// </summary>
    private static int? RankOf(Guid studentId, IReadOnlyDictionary<Guid, int> scoreByStudent)
        => scoreByStudent.TryGetValue(studentId, out var mine)
            ? 1 + scoreByStudent.Values.Count(other => other > mine)
            : null;

    /// <summary>Everyone who answered or finished a counted quiz, with their latest known name.</summary>
    private static Dictionary<Guid, string> Participants(
        List<QuizAnswer> answers,
        List<QuizSubmission> submissions,
        HashSet<Guid> countedIds)
        => answers
            .Where(a => countedIds.Contains(a.QuizId))
            .Select(a => (a.StudentId, a.StudentName, At: a.AnsweredAtUtc))
            .Concat(submissions
                .Where(s => countedIds.Contains(s.QuizId))
                .Select(s => (s.StudentId, s.StudentName, At: s.SubmittedAtUtc)))
            .GroupBy(x => x.StudentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.At).First().StudentName);

    private static int ClassAverage(
        List<QuizAnswer> answers,
        List<QuizSubmission> submissions,
        HashSet<Guid> countedIds,
        int totalAvailable)
    {
        var participants = Participants(answers, submissions, countedIds);
        if (participants.Count == 0) return 0;

        var percentages = participants.Keys.Select(studentId =>
        {
            var score = answers
                .Where(a => a.StudentId == studentId && countedIds.Contains(a.QuizId))
                .Sum(a => a.PointsAwarded);
            return Percentage(score, totalAvailable);
        });

        return (int)Math.Round(percentages.Average());
    }

    private static List<SessionTrackingDto> BuildSessionTracking(
        List<Session> sessions,
        List<Quiz> counted,
        List<QuizAnswer> answers,
        List<QuizSubmission> submissions)
    {
        var quizzesBySession = counted.GroupBy(q => q.SessionId)
            .ToDictionary(g => g.Key, g => g.ToList());

        return sessions
            .Where(session => quizzesBySession.ContainsKey(session.Id))
            .Select(session =>
            {
                var sessionQuizzes = quizzesBySession[session.Id];
                var quizIds = sessionQuizzes.Select(q => q.Id).ToHashSet();
                var total = sessionQuizzes.Sum(q => q.Questions.Sum(x => x.Points));

                var took = answers.Where(a => quizIds.Contains(a.QuizId))
                    .Select(a => a.StudentId)
                    .Concat(submissions.Where(s => quizIds.Contains(s.QuizId)).Select(s => s.StudentId))
                    .Distinct()
                    .ToList();

                var average = took.Count == 0
                    ? 0
                    : (int)Math.Round(took.Average(studentId => Percentage(
                        answers.Where(a => a.StudentId == studentId && quizIds.Contains(a.QuizId))
                            .Sum(a => a.PointsAwarded),
                        total)));

                return new SessionTrackingDto(
                    session.Id, session.Title, session.ScheduledAtUtc, session.StartedAtUtc,
                    sessionQuizzes.Count, total, took.Count, average);
            })
            .OrderByDescending(s => s.ScheduledAtUtc)
            .ToList();
    }

    // --- helpers ----------------------------------------------------------------

    /// <summary>
    /// A finished quiz, question by question, with this student's choice alongside the right answer.
    ///
    /// Only ever called for a quiz that is no longer being answered. Every question is listed,
    /// including the ones this student skipped — "you did not answer this, and here is what it was"
    /// is the most useful row in a review, and dropping it would make the numbering jump.
    /// </summary>
    private static List<MyQuestionReviewDto> BuildReview(Quiz quiz, List<QuizAnswer> mine)
    {
        var byQuestion = mine.ToDictionary(a => a.QuestionId);

        return quiz.Questions
            .OrderBy(q => q.Order)
            .Select(question =>
            {
                var answer = byQuestion.GetValueOrDefault(question.Id);
                return new MyQuestionReviewDto(
                    question.Id,
                    question.Order,
                    question.Text,
                    question.Points,
                    answer?.SelectedOptionId,
                    answer?.IsCorrect,
                    answer?.PointsAwarded ?? 0,
                    question.Options
                        .OrderBy(o => o.Order)
                        .Select(o => new MyOptionReviewDto(o.Id, o.Order, o.Text, o.IsCorrect))
                        .ToList());
            })
            .ToList();
    }

    /// <summary>
    /// A cancelled quiz's marks are preserved but excluded from every total. Expressed once, here,
    /// so the rule cannot be applied in some queries and forgotten in others.
    /// </summary>
    private static bool CountsTowardsMarks(Quiz quiz) => quiz.Status != QuizStatus.Cancelled;

    /// <summary>
    /// When the quiz closes FOR THIS STUDENT: their own extension if they have one, otherwise the
    /// class deadline.
    ///
    /// Extensions are stored as absolute deadlines and never earlier than the class one, so this is
    /// a max rather than a replacement — a class-wide extension granted after an individual one
    /// must not shorten the individual's time.
    /// </summary>
    private static DateTime? EffectiveDeadline(Quiz quiz, QuizExtension? extension)
    {
        if (quiz.ClosesAtUtc is null) return extension?.ClosesAtUtc;
        if (extension is null) return quiz.ClosesAtUtc;
        return extension.ClosesAtUtc > quiz.ClosesAtUtc ? extension.ClosesAtUtc : quiz.ClosesAtUtc;
    }

    private bool IsPastDeadline(Quiz quiz, QuizExtension? extension)
    {
        var deadline = EffectiveDeadline(quiz, extension);
        return QuizDeadline.IsPast(deadline, _clock.UtcNow, _settings.LateAnswerGraceSeconds);
    }

    private bool IsPastDeadline(Quiz quiz)
        => QuizDeadline.IsPast(quiz.ClosesAtUtc, _clock.UtcNow, _settings.LateAnswerGraceSeconds);

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

    /// <summary>
    /// The teacher's view of a live quiz, with everything that changes while it runs: the tallies,
    /// who is taking part, and whose clock has been extended. Gathered in one place because four
    /// call sites needed the same three reads and drifted apart the moment one of them changed.
    /// </summary>
    private async Task<QuizTeacherDto> BuildTeacherDtoAsync(Quiz quiz, CancellationToken ct)
    {
        var submissions = await _quizRepository.GetSubmissionsForQuizAsync(quiz.Id, ct);
        return ToTeacherDto(
            quiz,
            await _quizRepository.GetAnswersAsync(quiz.Id, ct),
            submissions.Count,
            submissions,
            await _quizRepository.GetExtensionsAsync(quiz.Id, ct));
    }

    private QuizTeacherDto ToTeacherDto(
        Quiz quiz,
        IReadOnlyCollection<QuizAnswer> answers,
        int submittedCount = 0,
        IReadOnlyCollection<QuizSubmission>? submissions = null,
        IReadOnlyCollection<QuizExtension>? extensions = null)
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
            submittedCount,
            BuildRespondents(quiz, answers, submissions, extensions),
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
    /// Who is taking part, from BOTH answers and submissions — a student who opened the quiz and
    /// finished without answering is exactly the one a teacher might be about to give more time to.
    ///
    /// Ordered with the unfinished first, because that is who the list is for.
    /// </summary>
    private List<QuizRespondentDto> BuildRespondents(
        Quiz quiz,
        IReadOnlyCollection<QuizAnswer> answers,
        IReadOnlyCollection<QuizSubmission>? submissions,
        IReadOnlyCollection<QuizExtension>? extensions)
    {
        submissions ??= [];
        extensions ??= [];
        if (answers.Count == 0 && submissions.Count == 0)
        {
            return [];
        }

        var submittedBy = submissions.ToDictionary(s => s.StudentId);
        var extraFor = extensions.ToDictionary(e => e.StudentId);

        var names = answers
            .Select(a => (a.StudentId, a.StudentName, At: a.AnsweredAtUtc))
            .Concat(submissions.Select(s => (s.StudentId, s.StudentName, At: s.SubmittedAtUtc)))
            .GroupBy(x => x.StudentId)
            // Latest wins, so a renamed student shows their current name.
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.At).First().StudentName);

        return names
            .Select(entry =>
            {
                var extension = extraFor.GetValueOrDefault(entry.Key);
                return new QuizRespondentDto(
                    entry.Key,
                    entry.Value,
                    answers.Count(a => a.StudentId == entry.Key),
                    submittedBy.ContainsKey(entry.Key),
                    EffectiveDeadline(quiz, extension),
                    extension is not null);
            })
            .OrderBy(r => r.HasSubmitted)
            .ThenBy(r => r.StudentName)
            .ToList();
    }

    /// <summary>
    /// Projects to the student shape. The option type here has no IsCorrect member at all, so the
    /// answer key cannot reach a browser by omission — only by someone deliberately changing the
    /// contract.
    /// </summary>
    private QuizStudentDto ToStudentDto(
        Quiz quiz,
        IReadOnlyCollection<QuizAnswer> myAnswers,
        DateTime? submittedAtUtc = null,
        QuizExtension? extension = null)
    {
        var mine = myAnswers.ToDictionary(a => a.QuestionId, a => a.SelectedOptionId);

        return new QuizStudentDto(
            quiz.Id,
            quiz.SessionId,
            quiz.Title,
            quiz.Status.ToString(),
            quiz.Questions.Sum(q => q.Points),
            // THEIR deadline, so an extended student's clock shows the time they actually have.
            // Sending the class deadline and correcting it later would show them a countdown that
            // hits zero while the server is still accepting their answers.
            EffectiveDeadline(quiz, extension),
            _clock.UtcNow,
            submittedAtUtc,
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

    /// <summary>
    /// Delegates to <see cref="ClassroomAccess.EnsureMemberAsync"/>. This was a private copy of
    /// that rule, identical to the four others in this service layer; see the reason there.
    /// </summary>
    private Task EnsureMemberAsync(Guid classroomId, Guid userId, CancellationToken ct)
        => ClassroomAccess.EnsureMemberAsync(
            _classroomRepository, _membershipRepository, classroomId, userId, ct);

    /// <summary>
    /// Membership is not enough to ANSWER: the answerer must be an enrolled student.
    ///
    /// <see cref="EnsureMemberAsync"/> deliberately counts the teacher as a member, which is right
    /// for reading a classroom's own material and wrong here. Answering is the one action where
    /// the teacher is not a participant, and every count on the live view is derived from the
    /// answer rows rather than from the roster — so a teacher who answered their own quiz became a
    /// respondent, appeared in their own results list, and watched "1 of 20 responded" that was
    /// themselves. The marks were never affected (tracking and ranking iterate the roster, which
    /// has no teacher in it), which is exactly why nothing surfaced it.
    ///
    /// The controller cannot express this: `SubmitAnswer`/`SubmitQuiz` carry no role because a
    /// role alone would not prove enrolment in THIS classroom. It has to be checked here.
    /// </summary>
    private async Task EnsureEnrolledStudentAsync(Guid classroomId, Guid userId, CancellationToken ct)
    {
        var classroom = await _classroomRepository.GetByIdAsync(classroomId, ct)
            ?? throw new KeyNotFoundException("Classroom not found.");

        if (classroom.TeacherId == userId)
        {
            throw new ForbiddenAccessException(
                "The classroom's teacher cannot take part in their own quiz.");
        }

        if (!await _membershipRepository.IsEnrolledAsync(classroomId, userId, ct))
        {
            throw new ForbiddenAccessException("You are not a member of this classroom.");
        }
    }
}
