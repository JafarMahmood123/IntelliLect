using ClassroomService.Application.Abstractions;
using ClassroomService.Application.Exceptions;
using ClassroomService.Application.DTOs.Quiz;
using ClassroomService.Application.Services;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;

namespace ClassroomService.UnitTests;

/// <summary>
/// The interleavings that only happen under real use (work-plan §11.7).
///
/// **These are not threaded tests, deliberately.** Spinning two tasks and hoping they collide
/// proves nothing on the runs where they do not, and a test that passes for timing reasons is
/// worse than no test. The interleavings that matter here are specific and nameable — "the
/// teacher's extension commits after the sweep decided which quizzes had run out, but before it
/// wrote" — so each one is driven exactly, through a hook on the unit of work that fires between
/// a caller's read phase and its save.
///
/// What that gives up is the thing a fake cannot model anyway: real transaction isolation. That
/// half is integration work and is recorded as such. What it catches is the half that lives in
/// the ordering of the code, which is where this project's race actually was.
/// </summary>
public sealed class QuizConcurrencyTests
{
    private static readonly Guid ClassroomId = Guid.NewGuid();
    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly Guid TeacherId = Guid.NewGuid();
    private static readonly Guid StudentId = Guid.NewGuid();
    private static readonly Guid OtherStudentId = Guid.NewGuid();

    private sealed record Harness(
        QuizService Service,
        QuizDeadlineSweeper Sweeper,
        FakeQuizRepository Quizzes,
        FakeUnitOfWork UnitOfWork,
        FakeClock Clock,
        RecordingQuizNotifier Notifier,
        FakeQuizSettings Settings);

    private static Harness Build()
    {
        var classrooms = new FakeClassroomRepository();
        classrooms.Seed(new Classroom { Id = ClassroomId, TeacherId = TeacherId, Name = "Physics" });

        var members = new FakeMembershipRepository();
        members.Enroll(ClassroomId, StudentId);
        members.Enroll(ClassroomId, OtherStudentId);

        var sessions = new FakeSessionRepository(new Session { Id = SessionId, ClassroomId = ClassroomId });
        var quizzes = new FakeQuizRepository();
        var notifier = new RecordingQuizNotifier();
        var clock = new FakeClock();
        var unitOfWork = new FakeUnitOfWork();
        var settings = new FakeQuizSettings();

        var service = new QuizService(
            quizzes, classrooms, members, sessions, notifier, new NoLiveAssistant(),
            unitOfWork, clock, settings, new RecordingLogger<QuizService>());

        // The sweeper runs in its own scope in production — its own repository instance, its own
        // unit of work. It is given the same fakes here so the two can act on one store, which is
        // what a shared database is.
        var sweeper = new QuizDeadlineSweeper(
            quizzes, notifier, unitOfWork, clock, settings,
            new RecordingLogger<QuizDeadlineSweeper>());

        return new Harness(service, sweeper, quizzes, unitOfWork, clock, notifier, settings);
    }

    /// <summary>Moves the fixed clock forward — the only "concurrency" these tests need.</summary>
    private static void Advance(Harness h, TimeSpan by) => h.Clock.UtcNow = h.Clock.UtcNow.Add(by);

    private static QuizDraftRequest Draft(int seconds = 60)
        => new("Check", [new QuestionDraftRequest(
            "Question", 5, seconds,
            [new OptionDraftRequest("Right", true), new OptionDraftRequest("Wrong", false)])]);

    private static async Task<QuizTeacherDto> PublishedAsync(Harness h, int seconds = 60)
    {
        var draft = await h.Service.CreateDraftAsync(ClassroomId, SessionId, TeacherId, Draft(seconds));
        return await h.Service.PublishAsync(ClassroomId, draft.Id, TeacherId);
    }

    // --- the sweep against a teacher granting more time -------------------------------

    [Fact]
    public async Task An_extension_granted_while_the_sweep_is_running_is_not_overwritten()
    {
        // The race the plan names, and the one that had a real consequence. The sweep reads which
        // quizzes have run out, then queries their extensions, then writes Closed. A teacher
        // watching the timer run down grants more time in between — which is precisely WHEN a
        // teacher does it. The sweep's copy of the quiz still holds the old deadline, so it closes
        // a quiz that has just been extended: the class is cut off mid-question and handed the
        // answer key, because closing is what releases the review.
        var h = Build();
        var quiz = await PublishedAsync(h, seconds: 60);

        // Time is up, and past the grace, so the sweep will pick it up.
        Advance(h, TimeSpan.FromSeconds(60 + h.Settings.LateAnswerGraceSeconds + 1));

        h.Quizzes.AfterReadingExtensions = async () =>
            await h.Service.ExtendAsync(
                ClassroomId, quiz.Id, TeacherId, new ExtendQuizRequest(120, null));

        var closed = await h.Sweeper.SweepAsync();

        Assert.Equal(0, closed);
        Assert.Equal(QuizStatus.Open, h.Quizzes.Find(quiz.Id)!.Status);
    }

    [Fact]
    public async Task The_extension_the_teacher_granted_is_the_deadline_that_survives()
    {
        // Not enough that the quiz stayed Open — the extra time must be the time the teacher
        // actually gave. A sweep that left the status alone but stamped ClosedAtUtc, or that
        // re-applied the old deadline, would look correct in the assertion above.
        var h = Build();
        var quiz = await PublishedAsync(h, seconds: 60);
        Advance(h, TimeSpan.FromSeconds(60 + h.Settings.LateAnswerGraceSeconds + 1));
        var expected = h.Clock.UtcNow.AddSeconds(120);

        h.Quizzes.AfterReadingExtensions = async () =>
            await h.Service.ExtendAsync(
                ClassroomId, quiz.Id, TeacherId, new ExtendQuizRequest(120, null));

        await h.Sweeper.SweepAsync();

        var stored = h.Quizzes.Find(quiz.Id)!;
        Assert.Equal(expected, stored.ClosesAtUtc);
        Assert.Null(stored.ClosedAtUtc);
    }

    [Fact]
    public async Task A_quiz_that_was_not_extended_still_closes_in_the_same_sweep()
    {
        // The reprieve must be per quiz. Skipping the whole batch because one of them was extended
        // would leave every other timed-out quiz open until the next sweep — and "the fix stopped
        // the sweep working" is the ordinary way a race fix goes wrong.
        var h = Build();
        var extended = await PublishedAsync(h, seconds: 60);
        var untouched = await PublishedAsync(h, seconds: 60);
        Advance(h, TimeSpan.FromSeconds(60 + h.Settings.LateAnswerGraceSeconds + 1));

        h.Quizzes.AfterReadingExtensions = async () =>
            await h.Service.ExtendAsync(
                ClassroomId, extended.Id, TeacherId, new ExtendQuizRequest(120, null));

        var closed = await h.Sweeper.SweepAsync();

        Assert.Equal(1, closed);
        Assert.Equal(QuizStatus.Open, h.Quizzes.Find(extended.Id)!.Status);
        Assert.Equal(QuizStatus.Closed, h.Quizzes.Find(untouched.Id)!.Status);
    }

    [Fact]
    public async Task A_reprieved_quiz_is_not_announced_as_closed()
    {
        // The broadcast is what every client acts on. Announcing a close that did not happen would
        // make each of them re-read and show a quiz that is still open as finished — the visible
        // half of the same bug, and it would outlive a fix to the status alone.
        var h = Build();
        var quiz = await PublishedAsync(h, seconds: 60);
        Advance(h, TimeSpan.FromSeconds(60 + h.Settings.LateAnswerGraceSeconds + 1));

        h.Quizzes.AfterReadingExtensions = async () =>
            await h.Service.ExtendAsync(
                ClassroomId, quiz.Id, TeacherId, new ExtendQuizRequest(120, null));

        await h.Sweeper.SweepAsync();

        Assert.DoesNotContain(h.Notifier.Notifications, call => call.State == nameof(QuizStatus.Closed));
    }

    [Fact]
    public async Task An_ordinary_sweep_with_nothing_racing_still_closes_the_quiz()
    {
        // The control. Every assertion above is about something NOT happening, and all of them
        // would pass if the sweep had simply stopped closing anything.
        var h = Build();
        var quiz = await PublishedAsync(h, seconds: 60);
        Advance(h, TimeSpan.FromSeconds(60 + h.Settings.LateAnswerGraceSeconds + 1));

        var closed = await h.Sweeper.SweepAsync();

        Assert.Equal(1, closed);
        Assert.Equal(QuizStatus.Closed, h.Quizzes.Find(quiz.Id)!.Status);
        Assert.Contains(h.Notifier.Notifications, call => call.State == nameof(QuizStatus.Closed));
    }

    // --- the same student submitting twice --------------------------------------------

    [Fact]
    public async Task Submitting_twice_records_one_submission()
    {
        // Two tabs, or a double-click, or a retry after a response that never arrived. The client
        // cannot tell those apart from a failure, so it will send again.
        var h = Build();
        var quiz = await PublishedAsync(h);

        await h.Service.SubmitQuizAsync(ClassroomId, quiz.Id, StudentId, "Sara");
        await h.Service.SubmitQuizAsync(ClassroomId, quiz.Id, StudentId, "Sara");

        Assert.Single(h.Quizzes.Submissions);
    }

    [Fact]
    public async Task A_repeat_submission_returns_the_original_time_rather_than_a_conflict()
    {
        // Telling a student "you already submitted" as an ERROR after their own retry is the
        // wrong outcome twice over: their work is safe, and the message says it is not.
        var h = Build();
        var quiz = await PublishedAsync(h);

        var first = await h.Service.SubmitQuizAsync(ClassroomId, quiz.Id, StudentId, "Sara");
        Advance(h, TimeSpan.FromSeconds(5));
        var second = await h.Service.SubmitQuizAsync(ClassroomId, quiz.Id, StudentId, "Sara");

        Assert.Equal(first.SubmittedAtUtc, second.SubmittedAtUtc);
    }

    [Fact]
    public async Task A_submission_landing_while_another_student_submits_does_not_displace_it()
    {
        // Interleaved submissions from two students. One store, two callers — the case where a
        // shared collection gets overwritten rather than appended to.
        var h = Build();
        var quiz = await PublishedAsync(h);

        h.UnitOfWork.BeforeSave = async () =>
            await h.Service.SubmitQuizAsync(ClassroomId, quiz.Id, OtherStudentId, "Ali");

        await h.Service.SubmitQuizAsync(ClassroomId, quiz.Id, StudentId, "Sara");

        Assert.Equal(2, h.Quizzes.Submissions.Count);
        Assert.Contains(h.Quizzes.Submissions, s => s.StudentId == StudentId);
        Assert.Contains(h.Quizzes.Submissions, s => s.StudentId == OtherStudentId);
    }

    [Fact]
    public async Task Answering_the_same_question_twice_updates_rather_than_accumulates()
    {
        // Changing your mind is legitimate right up until you submit, so the second answer must
        // replace the first. Two rows would double-count the marks — the unique index refuses
        // them in the real database, which means this path would 500 rather than mis-mark, but
        // both are failures of the same check.
        var h = Build();
        var quiz = await PublishedAsync(h);
        var question = quiz.Questions[0];

        await h.Service.SubmitAnswerAsync(
            ClassroomId, quiz.Id, StudentId, "Sara",
            new SubmitAnswerRequest(question.Id, question.Options[1].Id));
        await h.Service.SubmitAnswerAsync(
            ClassroomId, quiz.Id, StudentId, "Sara",
            new SubmitAnswerRequest(question.Id, question.Options[0].Id));

        var answer = Assert.Single(h.Quizzes.Answers);
        Assert.Equal(question.Options[0].Id, answer.SelectedOptionId);
        Assert.True(answer.IsCorrect);
    }

    // --- a submission landing exactly at the deadline ----------------------------------

    [Fact]
    public async Task An_answer_inside_the_grace_is_accepted()
    {
        // The grace exists because a click at T-0.5s can land at T+0.3s. Rejecting it punishes
        // network latency, not lateness.
        var h = Build();
        var quiz = await PublishedAsync(h, seconds: 60);
        var question = quiz.Questions[0];
        Advance(h, TimeSpan.FromSeconds(60 + h.Settings.LateAnswerGraceSeconds - 1));

        var result = await h.Service.SubmitAnswerAsync(
            ClassroomId, quiz.Id, StudentId, "Sara",
            new SubmitAnswerRequest(question.Id, question.Options[0].Id));

        Assert.Equal(question.Id, result.QuestionId);
    }

    [Fact]
    public async Task An_answer_past_the_grace_is_refused_even_though_the_quiz_is_still_Open()
    {
        // The window this closes: the sweep runs on a timer, so between the deadline passing and
        // the sweep noticing, the quiz is still Open in the database. The deadline has to be the
        // authority, not the status, or those seconds are free extra time for whoever is quickest.
        var h = Build();
        var quiz = await PublishedAsync(h, seconds: 60);
        var question = quiz.Questions[0];
        Advance(h, TimeSpan.FromSeconds(60 + h.Settings.LateAnswerGraceSeconds + 1));

        Assert.Equal(QuizStatus.Open, h.Quizzes.Find(quiz.Id)!.Status);
        await Assert.ThrowsAsync<ConflictException>(() => h.Service.SubmitAnswerAsync(
            ClassroomId, quiz.Id, StudentId, "Sara",
            new SubmitAnswerRequest(question.Id, question.Options[0].Id)));
    }

    [Fact]
    public async Task The_sweep_and_the_answer_path_agree_on_where_the_deadline_is()
    {
        // Both apply the same grace, and they must, in the same direction. If the sweep closed
        // earlier than the answer path accepts, a student inside the grace would be refused by a
        // quiz that had just been closed underneath them; if it closed later, the reverse. The
        // rule is one setting used twice — this pins that they cannot drift apart.
        var h = Build();
        var quiz = await PublishedAsync(h, seconds: 60);
        var question = quiz.Questions[0];

        // The last instant the answer path accepts.
        Advance(h, TimeSpan.FromSeconds(60 + h.Settings.LateAnswerGraceSeconds));
        await h.Service.SubmitAnswerAsync(
            ClassroomId, quiz.Id, StudentId, "Sara",
            new SubmitAnswerRequest(question.Id, question.Options[0].Id));

        // At that same instant the sweep must not yet have closed it.
        Assert.Equal(0, await h.Sweeper.SweepAsync());
        Assert.Equal(QuizStatus.Open, h.Quizzes.Find(quiz.Id)!.Status);
    }

    [Fact]
    public async Task A_student_with_extra_time_is_not_cut_off_by_the_class_deadline()
    {
        // The extension is per student, so the class's deadline passing must not close the quiz
        // underneath the one person still legitimately working.
        var h = Build();
        var quiz = await PublishedAsync(h, seconds: 60);
        var question = quiz.Questions[0];
        await h.Service.ExtendAsync(
            ClassroomId, quiz.Id, TeacherId, new ExtendQuizRequest(300, [StudentId]));

        Advance(h, TimeSpan.FromSeconds(60 + h.Settings.LateAnswerGraceSeconds + 1));

        Assert.Equal(0, await h.Sweeper.SweepAsync());
        await h.Service.SubmitAnswerAsync(
            ClassroomId, quiz.Id, StudentId, "Sara",
            new SubmitAnswerRequest(question.Id, question.Options[0].Id));
        await Assert.ThrowsAsync<ConflictException>(() => h.Service.SubmitAnswerAsync(
            ClassroomId, quiz.Id, OtherStudentId, "Ali",
            new SubmitAnswerRequest(question.Id, question.Options[0].Id)));
    }

    // --- the boundary itself ----------------------------------------------------------

    [Theory]
    [InlineData(0, false)]     // exactly the deadline
    [InlineData(3, false)]     // exactly the deadline plus the grace — still inside it
    [InlineData(4, true)]      // one second past
    public void One_rule_decides_when_a_quiz_is_over(int secondsPastDeadline, bool expectedPast)
    {
        // Pinned as a table because the interesting part is the boundary, and the boundary is
        // where the two callers used to disagree: the answer path asked `now > closesAt + grace`
        // while the sweep compared against a cutoff with `<=`, so at exactly closesAt + grace the
        // sweep closed a quiz whose answers the service was still accepting.
        var deadline = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        var past = QuizDeadline.IsPast(
            deadline, deadline.AddSeconds(secondsPastDeadline), graceSeconds: 3);

        Assert.Equal(expectedPast, past);
    }

    [Fact]
    public void A_quiz_with_no_timer_is_never_over()
    {
        Assert.False(QuizDeadline.IsPast(null, DateTime.UtcNow, graceSeconds: 3));
    }

    [Fact]
    public void A_negative_grace_is_treated_as_none_rather_than_bringing_the_deadline_forward()
    {
        // Misconfiguration must not silently make the quiz end EARLY, which is the direction that
        // takes time away from students.
        var deadline = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(QuizDeadline.IsPast(deadline, deadline, graceSeconds: -30));
        Assert.True(QuizDeadline.IsPast(deadline, deadline.AddSeconds(1), graceSeconds: -30));
    }

    private sealed class NoLiveAssistant : ILiveAssistantInternalClient
    {
        public Task<GeneratedQuizDto> GenerateQuizAsync(
            Guid sessionId, Guid classroomId, int questionCount, int minOptions, int maxOptions,
            IReadOnlyList<string>? avoid = null, bool wholeSession = false,
            CancellationToken ct = default)
            => throw new NotSupportedException("generation is not part of these tests");

        public Task<GeneratedQuestionDto> GenerateAnswersAsync(
            Guid sessionId, Guid classroomId, string questionText, int minOptions, int maxOptions,
            CancellationToken ct = default)
            => throw new NotSupportedException("generation is not part of these tests");

        public Task<int?> GetTranscriptSegmentCountAsync(Guid sessionId, CancellationToken ct = default)
            => Task.FromResult<int?>(0);
        public Task DeleteSessionTranscriptAsync(Guid sessionId, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<int> DeleteClassroomTranscriptsAsync(Guid classroomId, CancellationToken ct = default)
            => Task.FromResult(0);
    }
}
