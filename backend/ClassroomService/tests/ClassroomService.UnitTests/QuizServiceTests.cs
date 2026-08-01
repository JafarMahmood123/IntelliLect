using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Quiz;
using ClassroomService.Application.Exceptions;
using ClassroomService.Application.Services;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;

namespace ClassroomService.UnitTests;

/// <summary>
/// In-session quizzes. The rules worth protecting here are the ones that are invisible until they
/// break: that the answer key never reaches a student, that a closed or timed-out quiz stops
/// scoring, and that cancelling withdraws marks without destroying work.
/// </summary>
public sealed class QuizServiceTests
{
    private static readonly Guid ClassroomId = Guid.NewGuid();
    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly Guid TeacherId = Guid.NewGuid();
    private static readonly Guid StudentId = Guid.NewGuid();

    private sealed record Harness(
        QuizService Service,
        FakeQuizRepository Quizzes,
        RecordingQuizNotifier Notifier,
        FakeClock Clock,
        FakeLiveAssistant Assistant);

    /// <summary>
    /// Stands in for LiveAssistantService. Records the bounds it was called with, because the
    /// point of passing them is that the assistant cannot propose a quiz this service would refuse.
    /// </summary>
    private sealed class FakeLiveAssistant : ILiveAssistantInternalClient
    {
        public GeneratedQuizDto Result = new(
            "Generated",
            true,
            [new GeneratedQuestionDto("What was said?", [
                new GeneratedOptionDto("The right one", true),
                new GeneratedOptionDto("The wrong one", false),
            ])]);

        public Exception? Throws;
        public int Calls;
        public int LastQuestionCount;
        public int LastMinOptions;
        public int LastMaxOptions;

        public Task<GeneratedQuizDto> GenerateQuizAsync(
            Guid sessionId, Guid classroomId, int questionCount, int minOptions, int maxOptions,
            CancellationToken ct = default)
        {
            Calls++;
            LastQuestionCount = questionCount;
            LastMinOptions = minOptions;
            LastMaxOptions = maxOptions;
            if (Throws is not null) throw Throws;
            return Task.FromResult(Result);
        }

        public Task<int?> GetTranscriptSegmentCountAsync(Guid sessionId, CancellationToken ct = default)
            => Task.FromResult<int?>(0);
        public Task DeleteSessionTranscriptAsync(Guid sessionId, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<int> DeleteClassroomTranscriptsAsync(Guid classroomId, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private static Harness Build(FakeQuizSettings? settings = null)
    {
        var classrooms = new FakeClassroomRepository();
        classrooms.Seed(new Classroom { Id = ClassroomId, TeacherId = TeacherId, Name = "Physics" });

        var members = new FakeMembershipRepository();
        members.Enroll(ClassroomId, StudentId);

        var sessions = new FakeSessionRepository(new Session { Id = SessionId, ClassroomId = ClassroomId });
        var quizzes = new FakeQuizRepository();
        var notifier = new RecordingQuizNotifier();
        var clock = new FakeClock();

        var assistant = new FakeLiveAssistant();

        var service = new QuizService(
            quizzes, classrooms, members, sessions, notifier, assistant,
            new FakeUnitOfWork(), clock, settings ?? new FakeQuizSettings(),
            new RecordingLogger<QuizService>());

        return new Harness(service, quizzes, notifier, clock, assistant);
    }

    private static QuizDraftRequest Draft(
        int questions = 1, int optionsPerQuestion = 3, int points = 5, int seconds = 60,
        int correctCount = 1)
        => new("Check", Enumerable.Range(0, questions).Select(q =>
            new QuestionDraftRequest(
                $"Question {q}", points, seconds,
                Enumerable.Range(0, optionsPerQuestion)
                    .Select(o => new OptionDraftRequest($"Option {o}", o < correctCount))
                    .ToList())).ToList());

    private static async Task<QuizTeacherDto> PublishedAsync(Harness h)
    {
        var draft = await h.Service.CreateDraftAsync(ClassroomId, SessionId, TeacherId, Draft(), default);
        return await h.Service.PublishAsync(ClassroomId, draft.Id, TeacherId, default);
    }

    // --- composing and publishing ------------------------------------------------

    [Fact]
    public async Task Publishing_stamps_a_deadline_from_the_sum_of_the_question_times()
    {
        var h = Build();
        var draft = await h.Service.CreateDraftAsync(
            ClassroomId, SessionId, TeacherId, Draft(questions: 3, seconds: 40), default);

        var published = await h.Service.PublishAsync(ClassroomId, draft.Id, TeacherId, default);

        Assert.Equal("Open", published.Status);
        Assert.Equal(h.Clock.UtcNow.AddSeconds(120), published.ClosesAtUtc);
    }

    [Fact]
    public async Task Publishing_tells_the_room()
    {
        var h = Build();
        var published = await PublishedAsync(h);

        Assert.Equal((SessionId, published.Id, "Open"), Assert.Single(h.Notifier.Notifications));
    }

    [Fact]
    public async Task A_published_quiz_can_no_longer_be_edited()
    {
        // Rewriting the questions would silently invalidate answers already submitted against them.
        var h = Build();
        var published = await PublishedAsync(h);

        await Assert.ThrowsAsync<ConflictException>(
            () => h.Service.UpdateDraftAsync(ClassroomId, published.Id, TeacherId, Draft(), default));
    }

    [Theory]
    [InlineData(0, "no correct answer")]
    [InlineData(2, "two correct answers")]
    public async Task Publishing_requires_exactly_one_correct_answer(int correctCount, string _)
    {
        // Grading is an equality check; zero or several correct options makes the score impossible
        // or ambiguous, and it must fail before students see it rather than at marking time.
        var h = Build();
        var draft = await h.Service.CreateDraftAsync(
            ClassroomId, SessionId, TeacherId, Draft(correctCount: correctCount), default);

        await Assert.ThrowsAsync<ConflictException>(
            () => h.Service.PublishAsync(ClassroomId, draft.Id, TeacherId, default));
    }

    [Fact]
    public async Task Publishing_enforces_the_configured_limits()
    {
        var h = Build(new FakeQuizSettings { MaxQuestionsPerQuiz = 2 });
        var draft = await h.Service.CreateDraftAsync(
            ClassroomId, SessionId, TeacherId, Draft(questions: 3), default);

        await Assert.ThrowsAsync<ConflictException>(
            () => h.Service.PublishAsync(ClassroomId, draft.Id, TeacherId, default));
    }

    [Fact]
    public async Task Publishing_rejects_a_question_with_too_few_options()
    {
        var h = Build(new FakeQuizSettings { MinAnswersPerQuestion = 2 });
        var draft = await h.Service.CreateDraftAsync(
            ClassroomId, SessionId, TeacherId, Draft(optionsPerQuestion: 1), default);

        await Assert.ThrowsAsync<ConflictException>(
            () => h.Service.PublishAsync(ClassroomId, draft.Id, TeacherId, default));
    }

    // --- AI generation -------------------------------------------------------------

    [Fact]
    public async Task Generating_produces_a_draft_not_a_published_quiz()
    {
        // The whole safety story: the model proposes, the teacher disposes. A generated quiz that
        // went straight to Open would put unreviewed questions in front of a class.
        var h = Build();

        var draft = await h.Service.GenerateDraftAsync(ClassroomId, SessionId, TeacherId, 3, default);

        Assert.Equal("Draft", draft.Status);
        Assert.Equal("Generated", draft.Title);
        Assert.Single(draft.Questions);
    }

    [Fact]
    public async Task Generated_questions_keep_the_assistants_correct_answer()
    {
        var h = Build();

        var draft = await h.Service.GenerateDraftAsync(ClassroomId, SessionId, TeacherId, 3, default);

        var options = draft.Questions[0].Options;
        Assert.Equal(1, options.Count(o => o.IsCorrect));
        Assert.Equal("The right one", options.Single(o => o.IsCorrect).Text);
    }

    [Fact]
    public async Task Generated_questions_get_this_services_marks_and_timing()
    {
        // Marks are a pedagogical weight the teacher owns and seconds are already configured here,
        // so the model is never asked for either.
        var h = Build(new FakeQuizSettings { DefaultSecondsPerQuestion = 45 });

        var draft = await h.Service.GenerateDraftAsync(ClassroomId, SessionId, TeacherId, 3, default);

        Assert.Equal(1, draft.Questions[0].Points);
        Assert.Equal(45, draft.Questions[0].TimeLimitSeconds);
    }

    [Fact]
    public async Task Generation_is_asked_for_the_configured_answer_bounds()
    {
        // This service owns the limits; passing them is what stops the assistant proposing a quiz
        // that publish would then reject.
        var h = Build(new FakeQuizSettings { MinAnswersPerQuestion = 3, MaxAnswersPerQuestion = 5 });

        await h.Service.GenerateDraftAsync(ClassroomId, SessionId, TeacherId, 3, default);

        Assert.Equal(3, h.Assistant.LastMinOptions);
        Assert.Equal(5, h.Assistant.LastMaxOptions);
    }

    [Fact]
    public async Task Asking_for_more_questions_than_allowed_is_clamped_not_refused()
    {
        var h = Build(new FakeQuizSettings { MaxQuestionsPerQuiz = 4 });

        await h.Service.GenerateDraftAsync(ClassroomId, SessionId, TeacherId, 99, default);

        Assert.Equal(4, h.Assistant.LastQuestionCount);
    }

    [Fact]
    public async Task A_generated_draft_still_has_to_pass_publish_validation()
    {
        // Generation adds no second route to Open: the same gate that rejects a hand-written quiz
        // rejects a generated one.
        var h = Build();
        h.Assistant.Result = new GeneratedQuizDto(
            "Bad",
            true,
            [new GeneratedQuestionDto("Only one option", [new GeneratedOptionDto("a", true)])]);

        var draft = await h.Service.GenerateDraftAsync(ClassroomId, SessionId, TeacherId, 1, default);

        await Assert.ThrowsAsync<ConflictException>(
            () => h.Service.PublishAsync(ClassroomId, draft.Id, TeacherId, default));
    }

    [Fact]
    public async Task A_student_cannot_spend_a_generation()
    {
        // Authorised BEFORE the assistant is called: a model call costs money and time, and
        // refusing only afterwards would let anyone enrolled burn both.
        var h = Build();

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => h.Service.GenerateDraftAsync(ClassroomId, SessionId, StudentId, 3, default));

        Assert.Equal(0, h.Assistant.Calls);
    }

    [Fact]
    public async Task Generating_for_an_unknown_session_does_not_call_the_assistant()
    {
        var h = Build();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => h.Service.GenerateDraftAsync(ClassroomId, Guid.NewGuid(), TeacherId, 3, default));

        Assert.Equal(0, h.Assistant.Calls);
    }

    [Fact]
    public async Task An_assistant_failure_reaches_the_teacher()
    {
        var h = Build();
        h.Assistant.Throws = new ServiceUnavailableException("assistant down");

        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => h.Service.GenerateDraftAsync(ClassroomId, SessionId, TeacherId, 3, default));
    }

    [Fact]
    public async Task Nothing_to_quiz_on_yet_stays_a_conflict()
    {
        // Distinct from an outage: the teacher fixes this one by carrying on talking, so the two
        // must not collapse into a single message.
        var h = Build();
        h.Assistant.Throws = new ConflictException("nothing transcribed yet");

        await Assert.ThrowsAsync<ConflictException>(
            () => h.Service.GenerateDraftAsync(ClassroomId, SessionId, TeacherId, 3, default));
    }

    // --- the answer key must not leak --------------------------------------------

    [Fact]
    public async Task The_student_view_cannot_carry_which_option_is_correct()
    {
        // The guard is structural: QuizOptionStudentDto has no IsCorrect member, so leaking it
        // would take a deliberate contract change rather than an oversight. This asserts the
        // contract itself, which is what a future "just add a flag" would break.
        var h = Build();
        var published = await PublishedAsync(h);

        var studentView = await h.Service.GetForStudentAsync(ClassroomId, published.Id, StudentId, default);

        var optionType = studentView.Questions[0].Options[0].GetType();
        Assert.Null(optionType.GetProperty("IsCorrect"));
        Assert.DoesNotContain(
            optionType.GetProperties(),
            p => p.Name.Contains("Correct", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_draft_does_not_exist_as_far_as_a_student_is_concerned()
    {
        var h = Build();
        var draft = await h.Service.CreateDraftAsync(ClassroomId, SessionId, TeacherId, Draft(), default);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => h.Service.GetForStudentAsync(ClassroomId, draft.Id, StudentId, default));
    }

    [Fact]
    public async Task Submitting_does_not_reveal_whether_the_answer_was_right()
    {
        // Answers stay changeable until close, so a correct/incorrect acknowledgement would let a
        // student walk every option until the response said yes.
        var h = Build();
        var published = await PublishedAsync(h);
        var view = await h.Service.GetForStudentAsync(ClassroomId, published.Id, StudentId, default);

        var result = await h.Service.SubmitAnswerAsync(
            ClassroomId, published.Id, StudentId, "Amina",
            new SubmitAnswerRequest(view.Questions[0].Id, view.Questions[0].Options[0].Id), default);

        Assert.DoesNotContain(
            result.GetType().GetProperties(),
            p => p.Name.Contains("Correct", StringComparison.OrdinalIgnoreCase));
    }

    // --- answering and grading ----------------------------------------------------

    [Fact]
    public async Task A_correct_answer_scores_the_questions_marks_and_a_wrong_one_scores_nothing()
    {
        var h = Build();
        var published = await PublishedAsync(h);
        var teacherView = await h.Service.GetForTeacherAsync(ClassroomId, published.Id, TeacherId, default);
        var question = teacherView.Questions[0];
        var correct = question.Options.First(o => o.IsCorrect);
        var wrong = question.Options.First(o => !o.IsCorrect);

        await h.Service.SubmitAnswerAsync(
            ClassroomId, published.Id, StudentId, "Amina", new SubmitAnswerRequest(question.Id, correct.Id), default);
        Assert.Equal(5, h.Quizzes.Answers.Single().PointsAwarded);

        await h.Service.SubmitAnswerAsync(
            ClassroomId, published.Id, StudentId, "Amina", new SubmitAnswerRequest(question.Id, wrong.Id), default);
        Assert.Equal(0, h.Quizzes.Answers.Single().PointsAwarded);
    }

    [Fact]
    public async Task Changing_an_answer_updates_the_same_row_rather_than_adding_another()
    {
        // One row per (question, student) is what the unique index guarantees in the database;
        // accumulating rows here would double-count a student who changed their mind.
        var h = Build();
        var published = await PublishedAsync(h);
        var view = await h.Service.GetForStudentAsync(ClassroomId, published.Id, StudentId, default);
        var question = view.Questions[0];

        foreach (var option in question.Options)
        {
            await h.Service.SubmitAnswerAsync(
                ClassroomId, published.Id, StudentId, "Amina", new SubmitAnswerRequest(question.Id, option.Id), default);
        }

        Assert.Single(h.Quizzes.Answers);
        Assert.Equal(question.Options.Last().Id, h.Quizzes.Answers[0].SelectedOptionId);
    }

    [Fact]
    public async Task A_student_reload_shows_what_they_already_picked()
    {
        var h = Build();
        var published = await PublishedAsync(h);
        var view = await h.Service.GetForStudentAsync(ClassroomId, published.Id, StudentId, default);
        var chosen = view.Questions[0].Options[1];

        await h.Service.SubmitAnswerAsync(
            ClassroomId, published.Id, StudentId, "Amina",
            new SubmitAnswerRequest(view.Questions[0].Id, chosen.Id), default);

        var reloaded = await h.Service.GetForStudentAsync(ClassroomId, published.Id, StudentId, default);
        Assert.Equal(chosen.Id, reloaded.Questions[0].SelectedOptionId);
    }

    // --- the deadline is the authority -------------------------------------------

    [Fact]
    public async Task An_answer_inside_the_grace_period_is_still_accepted()
    {
        // An answer clicked at T-0.5s can arrive at T+0.3s. Rejecting it punishes latency.
        var h = Build(new FakeQuizSettings { LateAnswerGraceSeconds = 3 });
        var published = await PublishedAsync(h);
        var view = await h.Service.GetForStudentAsync(ClassroomId, published.Id, StudentId, default);

        h.Clock.UtcNow = published.ClosesAtUtc!.Value.AddSeconds(2);

        var result = await h.Service.SubmitAnswerAsync(
            ClassroomId, published.Id, StudentId, "Amina",
            new SubmitAnswerRequest(view.Questions[0].Id, view.Questions[0].Options[0].Id), default);

        Assert.NotEqual(default, result.AnsweredAtUtc);
    }

    [Fact]
    public async Task An_answer_past_the_deadline_is_refused_even_though_the_status_still_says_Open()
    {
        // Nothing schedules a close, so a timed-out quiz IS still Open in the database. The
        // deadline check on the write path is the only thing stopping it from scoring forever.
        var h = Build(new FakeQuizSettings { LateAnswerGraceSeconds = 3 });
        var published = await PublishedAsync(h);
        var view = await h.Service.GetForStudentAsync(ClassroomId, published.Id, StudentId, default);

        h.Clock.UtcNow = published.ClosesAtUtc!.Value.AddSeconds(30);
        Assert.Equal(QuizStatus.Open, h.Quizzes.Find(published.Id)!.Status);

        await Assert.ThrowsAsync<ConflictException>(
            () => h.Service.SubmitAnswerAsync(
                ClassroomId, published.Id, StudentId, "Amina",
                new SubmitAnswerRequest(view.Questions[0].Id, view.Questions[0].Options[0].Id), default));
    }

    [Fact]
    public async Task A_timed_out_quiz_is_not_offered_to_someone_joining_late()
    {
        var h = Build();
        var published = await PublishedAsync(h);
        h.Clock.UtcNow = published.ClosesAtUtc!.Value.AddMinutes(1);

        Assert.Null(await h.Service.GetOpenForSessionAsync(ClassroomId, SessionId, StudentId, default));
    }

    [Fact]
    public async Task Someone_joining_mid_quiz_is_given_the_open_one()
    {
        var h = Build();
        var published = await PublishedAsync(h);

        var found = await h.Service.GetOpenForSessionAsync(ClassroomId, SessionId, StudentId, default);

        Assert.Equal(published.Id, found!.Id);
    }

    [Fact]
    public async Task A_closed_quiz_stops_accepting_answers()
    {
        var h = Build();
        var published = await PublishedAsync(h);
        var view = await h.Service.GetForStudentAsync(ClassroomId, published.Id, StudentId, default);
        await h.Service.CloseAsync(ClassroomId, published.Id, TeacherId, default);

        await Assert.ThrowsAsync<ConflictException>(
            () => h.Service.SubmitAnswerAsync(
                ClassroomId, published.Id, StudentId, "Amina",
                new SubmitAnswerRequest(view.Questions[0].Id, view.Questions[0].Options[0].Id), default));
    }

    // --- cancelling ---------------------------------------------------------------

    [Fact]
    public async Task Cancelling_keeps_every_answer_but_stops_them_counting()
    {
        // The whole point of cancel-not-delete: a teacher withdrawing a bad question must not
        // destroy the work of everyone who already answered it.
        var h = Build();
        var published = await PublishedAsync(h);
        var view = await h.Service.GetForStudentAsync(ClassroomId, published.Id, StudentId, default);
        await h.Service.SubmitAnswerAsync(
            ClassroomId, published.Id, StudentId, "Amina",
            new SubmitAnswerRequest(view.Questions[0].Id, view.Questions[0].Options[0].Id), default);

        await h.Service.CancelAsync(ClassroomId, published.Id, TeacherId, default);

        Assert.Single(h.Quizzes.Answers);
        var results = await h.Service.GetResultsAsync(ClassroomId, published.Id, TeacherId, default);
        Assert.False(results.CountsTowardsMarks);
    }

    [Fact]
    public async Task A_cancelled_quiz_refuses_further_answers()
    {
        var h = Build();
        var published = await PublishedAsync(h);
        var view = await h.Service.GetForStudentAsync(ClassroomId, published.Id, StudentId, default);
        await h.Service.CancelAsync(ClassroomId, published.Id, TeacherId, default);

        await Assert.ThrowsAsync<ConflictException>(
            () => h.Service.SubmitAnswerAsync(
                ClassroomId, published.Id, StudentId, "Amina",
                new SubmitAnswerRequest(view.Questions[0].Id, view.Questions[0].Options[0].Id), default));
    }

    [Fact]
    public async Task A_draft_can_be_cancelled_and_cancelling_is_terminal()
    {
        var h = Build();
        var draft = await h.Service.CreateDraftAsync(ClassroomId, SessionId, TeacherId, Draft(), default);

        var cancelled = await h.Service.CancelAsync(ClassroomId, draft.Id, TeacherId, default);
        Assert.Equal("Cancelled", cancelled.Status);

        await Assert.ThrowsAsync<ConflictException>(
            () => h.Service.CancelAsync(ClassroomId, draft.Id, TeacherId, default));
        await Assert.ThrowsAsync<ConflictException>(
            () => h.Service.PublishAsync(ClassroomId, draft.Id, TeacherId, default));
    }

    // --- authorization ------------------------------------------------------------

    [Fact]
    public async Task Only_the_classrooms_own_teacher_can_manage_a_quiz()
    {
        // Another teacher holds a valid Teacher token — the role check alone is not enough.
        var h = Build();
        var published = await PublishedAsync(h);

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => h.Service.CancelAsync(ClassroomId, published.Id, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task A_non_member_cannot_see_or_answer_a_quiz()
    {
        var h = Build();
        var published = await PublishedAsync(h);

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => h.Service.GetForStudentAsync(ClassroomId, published.Id, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task A_quiz_from_another_classroom_is_not_found_rather_than_forbidden()
    {
        // 404 not 403: whether a quiz exists elsewhere is itself information.
        var h = Build();
        var published = await PublishedAsync(h);
        h.Quizzes.Find(published.Id)!.ClassroomId = Guid.NewGuid();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => h.Service.GetForTeacherAsync(ClassroomId, published.Id, TeacherId, default));
    }

    // --- results -------------------------------------------------------------------

    [Fact]
    public async Task A_student_is_not_shown_their_score_until_the_quiz_closes()
    {
        var h = Build();
        var published = await PublishedAsync(h);
        var view = await h.Service.GetForStudentAsync(ClassroomId, published.Id, StudentId, default);
        var correctId = (await h.Service.GetForTeacherAsync(ClassroomId, published.Id, TeacherId, default))
            .Questions[0].Options.First(o => o.IsCorrect).Id;
        await h.Service.SubmitAnswerAsync(
            ClassroomId, published.Id, StudentId, "Amina", new SubmitAnswerRequest(view.Questions[0].Id, correctId), default);

        var whileOpen = await h.Service.GetMyResultAsync(ClassroomId, published.Id, StudentId, default);
        Assert.Equal(0, whileOpen.Score);
        Assert.Null(whileOpen.Answers[0].IsCorrect);

        await h.Service.CloseAsync(ClassroomId, published.Id, TeacherId, default);

        var afterClose = await h.Service.GetMyResultAsync(ClassroomId, published.Id, StudentId, default);
        Assert.Equal(5, afterClose.Score);
        Assert.True(afterClose.Answers[0].IsCorrect);
    }

    // --- session-wide summaries ---------------------------------------------------

    [Fact]
    public async Task The_teacher_summary_names_each_student_and_totals_their_marks()
    {
        var h = Build();
        var published = await PublishedAsync(h);
        var teacherView = await h.Service.GetForTeacherAsync(ClassroomId, published.Id, TeacherId, default);
        var correct = teacherView.Questions[0].Options.First(o => o.IsCorrect).Id;

        await h.Service.SubmitAnswerAsync(
            ClassroomId, published.Id, StudentId, "Amina",
            new SubmitAnswerRequest(teacherView.Questions[0].Id, correct), default);
        await h.Service.CloseAsync(ClassroomId, published.Id, TeacherId, default);

        var summary = await h.Service.GetSessionSummaryAsync(ClassroomId, SessionId, TeacherId, default);

        var student = Assert.Single(summary.Students);
        Assert.Equal("Amina", student.StudentName);
        Assert.Equal(5, student.Score);
        Assert.Equal(100, student.Percentage);
    }

    [Fact]
    public async Task The_teacher_summary_breaks_each_question_down_by_option()
    {
        // The option tallies are what explain WHY a question went badly — a wrong option taking
        // most of the votes is a misconception, not a class that failed.
        var h = Build();
        var published = await PublishedAsync(h);
        var teacherView = await h.Service.GetForTeacherAsync(ClassroomId, published.Id, TeacherId, default);
        var wrong = teacherView.Questions[0].Options.First(o => !o.IsCorrect).Id;

        await h.Service.SubmitAnswerAsync(
            ClassroomId, published.Id, StudentId, "Amina",
            new SubmitAnswerRequest(teacherView.Questions[0].Id, wrong), default);

        var summary = await h.Service.GetSessionSummaryAsync(ClassroomId, SessionId, TeacherId, default);

        var question = Assert.Single(summary.Questions);
        Assert.Equal(1, question.AnsweredCount);
        Assert.Equal(0, question.CorrectCount);
        Assert.Equal(1, question.Options.Single(o => o.OptionId == wrong).SelectedCount);
    }

    [Fact]
    public async Task A_cancelled_quiz_is_still_listed_but_drops_out_of_the_totals()
    {
        // Hiding it entirely would look like a bug to the teacher who cancelled it; counting it
        // would defeat the point of cancelling.
        var h = Build();
        var published = await PublishedAsync(h);
        var teacherView = await h.Service.GetForTeacherAsync(ClassroomId, published.Id, TeacherId, default);
        var correct = teacherView.Questions[0].Options.First(o => o.IsCorrect).Id;
        await h.Service.SubmitAnswerAsync(
            ClassroomId, published.Id, StudentId, "Amina",
            new SubmitAnswerRequest(teacherView.Questions[0].Id, correct), default);

        await h.Service.CancelAsync(ClassroomId, published.Id, TeacherId, default);

        var summary = await h.Service.GetSessionSummaryAsync(ClassroomId, SessionId, TeacherId, default);
        Assert.Equal(1, summary.QuizCount);
        Assert.Equal(0, summary.CountedQuizCount);
        Assert.Equal(0, summary.TotalPointsAvailable);
        Assert.False(Assert.Single(summary.Questions).CountsTowardsMarks);
    }

    [Fact]
    public async Task A_draft_never_appears_in_the_summary()
    {
        var h = Build();
        await h.Service.CreateDraftAsync(ClassroomId, SessionId, TeacherId, Draft(), default);

        var summary = await h.Service.GetSessionSummaryAsync(ClassroomId, SessionId, TeacherId, default);

        Assert.Equal(0, summary.QuizCount);
        Assert.Empty(summary.Questions);
    }

    [Fact]
    public async Task A_students_own_summary_shows_their_marks_only_once_the_quiz_closes()
    {
        var h = Build();
        var published = await PublishedAsync(h);
        var teacherView = await h.Service.GetForTeacherAsync(ClassroomId, published.Id, TeacherId, default);
        var correct = teacherView.Questions[0].Options.First(o => o.IsCorrect).Id;
        await h.Service.SubmitAnswerAsync(
            ClassroomId, published.Id, StudentId, "Amina",
            new SubmitAnswerRequest(teacherView.Questions[0].Id, correct), default);

        var whileOpen = await h.Service.GetMySessionSummaryAsync(ClassroomId, SessionId, StudentId, default);
        Assert.Equal(0, whileOpen.Score);
        Assert.Equal(1, Assert.Single(whileOpen.Quizzes).AnsweredCount);

        await h.Service.CloseAsync(ClassroomId, published.Id, TeacherId, default);

        var afterClose = await h.Service.GetMySessionSummaryAsync(ClassroomId, SessionId, StudentId, default);
        Assert.Equal(5, afterClose.Score);
        Assert.Equal(100, afterClose.Percentage);
    }

    [Fact]
    public async Task A_student_cannot_read_the_teachers_session_summary()
    {
        var h = Build();
        await PublishedAsync(h);

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => h.Service.GetSessionSummaryAsync(ClassroomId, SessionId, StudentId, default));
    }
}
