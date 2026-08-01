using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Quiz;
using ClassroomService.Application.Services;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;

namespace ClassroomService.UnitTests;

/// <summary>
/// A quiz reaching its deadline is the NORMAL end of one, not an error state. Until this existed
/// the deadline only refused answers: the quiz stayed Open, so the class never saw their marks and
/// the teacher could not start another one. The rules worth protecting are that it closes at the
/// right moment, that it closes exactly as the teacher's own button does, and that it never
/// touches a quiz that is still running.
/// </summary>
public sealed class QuizDeadlineSweeperTests
{
    private static readonly Guid ClassroomId = Guid.NewGuid();
    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly Guid TeacherId = Guid.NewGuid();
    private static readonly Guid StudentId = Guid.NewGuid();

    private sealed record Harness(
        QuizService Service,
        QuizDeadlineSweeper Sweeper,
        FakeQuizRepository Quizzes,
        RecordingQuizNotifier Notifier,
        FakeClock Clock);

    private static Harness Build(FakeQuizSettings? settings = null)
    {
        var quizSettings = settings ?? new FakeQuizSettings();

        var classrooms = new FakeClassroomRepository();
        classrooms.Seed(new Classroom { Id = ClassroomId, TeacherId = TeacherId, Name = "Physics" });

        var members = new FakeMembershipRepository();
        members.Enroll(ClassroomId, StudentId);

        var sessions = new FakeSessionRepository(
            new Session { Id = SessionId, ClassroomId = ClassroomId });
        var quizzes = new FakeQuizRepository();
        var notifier = new RecordingQuizNotifier();
        var clock = new FakeClock();
        var unitOfWork = new FakeUnitOfWork();

        var service = new QuizService(
            quizzes, classrooms, members, sessions, notifier, new FakeLiveAssistantStub(),
            unitOfWork, clock, quizSettings, new RecordingLogger<QuizService>());

        var sweeper = new QuizDeadlineSweeper(
            quizzes, notifier, unitOfWork, clock, quizSettings,
            new RecordingLogger<QuizDeadlineSweeper>());

        return new Harness(service, sweeper, quizzes, notifier, clock);
    }

    /// <summary>Generation is never exercised here; the quizzes are composed by hand.</summary>
    private sealed class FakeLiveAssistantStub : ILiveAssistantInternalClient
    {
        public Task<GeneratedQuizDto> GenerateQuizAsync(
            Guid sessionId, Guid classroomId, int questionCount, int minOptions, int maxOptions,
            IReadOnlyList<string>? avoid = null, bool wholeSession = false,
            CancellationToken ct = default)
            => throw new NotSupportedException("The deadline sweep does not generate quizzes.");

        public Task<GeneratedQuestionDto> GenerateAnswersAsync(
            Guid sessionId, Guid classroomId, string questionText, int minOptions, int maxOptions,
            CancellationToken ct = default)
            => throw new NotSupportedException("The deadline sweep does not generate answers.");

        public Task<int?> GetTranscriptSegmentCountAsync(Guid sessionId, CancellationToken ct = default)
            => Task.FromResult<int?>(0);
        public Task DeleteSessionTranscriptAsync(Guid sessionId, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<int> DeleteClassroomTranscriptsAsync(Guid classroomId, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private static QuizDraftRequest Draft(int seconds = 60)
        => new("Check", [
            new QuestionDraftRequest("Question 0", 5, seconds, [
                new OptionDraftRequest("Right", true),
                new OptionDraftRequest("Wrong", false),
            ]),
        ]);

    private static async Task<QuizTeacherDto> PublishedAsync(Harness h, int seconds = 60)
    {
        var draft = await h.Service.CreateDraftAsync(
            ClassroomId, SessionId, TeacherId, Draft(seconds), default);
        return await h.Service.PublishAsync(ClassroomId, draft.Id, TeacherId, default);
    }

    [Fact]
    public async Task A_quiz_whose_time_has_run_out_is_closed()
    {
        var h = Build();
        var published = await PublishedAsync(h);
        h.Clock.UtcNow = published.ClosesAtUtc!.Value.AddSeconds(30);

        var closed = await h.Sweeper.SweepAsync();

        Assert.Equal(1, closed);
        Assert.Equal(QuizStatus.Closed, h.Quizzes.Find(published.Id)!.Status);
    }

    [Fact]
    public async Task A_quiz_still_within_its_time_is_left_alone()
    {
        var h = Build();
        var published = await PublishedAsync(h);
        h.Clock.UtcNow = published.ClosesAtUtc!.Value.AddSeconds(-1);

        Assert.Equal(0, await h.Sweeper.SweepAsync());
        Assert.Equal(QuizStatus.Open, h.Quizzes.Find(published.Id)!.Status);
    }

    [Fact]
    public async Task The_late_answer_grace_is_honoured_before_closing()
    {
        // The answer path accepts a click at T-0.5s that lands at T+0.3s. Closing at the raw
        // deadline would refuse it — punishing network latency, which is what the grace prevents.
        var h = Build(new FakeQuizSettings { LateAnswerGraceSeconds = 10 });
        var published = await PublishedAsync(h);

        h.Clock.UtcNow = published.ClosesAtUtc!.Value.AddSeconds(5);
        Assert.Equal(0, await h.Sweeper.SweepAsync());

        h.Clock.UtcNow = published.ClosesAtUtc!.Value.AddSeconds(11);
        Assert.Equal(1, await h.Sweeper.SweepAsync());
    }

    [Fact]
    public async Task Closing_on_time_releases_the_students_marks()
    {
        // The whole reason this exists. Before it, a class that ran out of time never saw a mark,
        // because marks are withheld until the quiz is no longer Open.
        var h = Build();
        var published = await PublishedAsync(h);
        var view = await h.Service.GetForStudentAsync(ClassroomId, published.Id, StudentId, default);
        var teacherView = await h.Service.GetForTeacherAsync(ClassroomId, published.Id, TeacherId, default);
        await h.Service.SubmitAnswerAsync(
            ClassroomId, published.Id, StudentId, "Amina",
            new SubmitAnswerRequest(
                view.Questions[0].Id,
                teacherView.Questions[0].Options.First(o => o.IsCorrect).Id),
            default);

        var whileOpen = await h.Service.GetMyResultAsync(ClassroomId, published.Id, StudentId, default);
        Assert.Equal(0, whileOpen.Score);

        h.Clock.UtcNow = published.ClosesAtUtc!.Value.AddSeconds(30);
        await h.Sweeper.SweepAsync();

        var afterSweep = await h.Service.GetMyResultAsync(ClassroomId, published.Id, StudentId, default);
        Assert.Equal(5, afterSweep.Score);
        Assert.True(afterSweep.Answers[0].IsCorrect);
    }

    [Fact]
    public async Task Closing_on_time_frees_the_composer()
    {
        // A quiz stuck Open kept blocking the panel, so a teacher whose timer expired could not
        // start another one until they remembered the button.
        var h = Build();
        var published = await PublishedAsync(h);
        h.Clock.UtcNow = published.ClosesAtUtc!.Value.AddSeconds(30);

        await h.Sweeper.SweepAsync();

        Assert.Null(await h.Service.GetOpenForSessionAsync(
            ClassroomId, SessionId, TeacherId, default));
    }

    [Fact]
    public async Task The_room_is_told_the_quiz_closed()
    {
        var h = Build();
        var published = await PublishedAsync(h);
        h.Notifier.Notifications.Clear();
        h.Clock.UtcNow = published.ClosesAtUtc!.Value.AddSeconds(30);

        await h.Sweeper.SweepAsync();

        Assert.Equal(
            (SessionId, published.Id, "Closed"), Assert.Single(h.Notifier.Notifications));
    }

    [Fact]
    public async Task The_close_is_stamped_at_the_deadline_not_at_the_sweep()
    {
        // A quiz that ran out at 10:05 was over at 10:05, whether the sweep ran a second later or
        // the service happened to be restarting.
        var h = Build();
        var published = await PublishedAsync(h);
        h.Clock.UtcNow = published.ClosesAtUtc!.Value.AddMinutes(20);

        await h.Sweeper.SweepAsync();

        Assert.Equal(published.ClosesAtUtc, h.Quizzes.Find(published.Id)!.ClosedAtUtc);
    }

    [Fact]
    public async Task A_quiz_the_teacher_already_closed_is_not_touched_again()
    {
        var h = Build();
        var published = await PublishedAsync(h);
        await h.Service.CloseAsync(ClassroomId, published.Id, TeacherId, default);
        h.Notifier.Notifications.Clear();
        h.Clock.UtcNow = published.ClosesAtUtc!.Value.AddSeconds(30);

        Assert.Equal(0, await h.Sweeper.SweepAsync());
        Assert.Empty(h.Notifier.Notifications);
    }

    [Fact]
    public async Task A_cancelled_quiz_is_not_reopened_or_reclosed()
    {
        // Cancelling withdraws it from marks. Closing it afterwards would quietly put it back.
        var h = Build();
        var published = await PublishedAsync(h);
        await h.Service.CancelAsync(ClassroomId, published.Id, TeacherId, default);
        h.Clock.UtcNow = published.ClosesAtUtc!.Value.AddSeconds(30);

        Assert.Equal(0, await h.Sweeper.SweepAsync());
        Assert.Equal(QuizStatus.Cancelled, h.Quizzes.Find(published.Id)!.Status);
    }

    [Fact]
    public async Task A_draft_has_no_deadline_and_is_never_closed()
    {
        var h = Build();
        var draft = await h.Service.CreateDraftAsync(
            ClassroomId, SessionId, TeacherId, Draft(), default);
        h.Clock.UtcNow = h.Clock.UtcNow.AddDays(1);

        Assert.Equal(0, await h.Sweeper.SweepAsync());
        Assert.Equal(QuizStatus.Draft, h.Quizzes.Find(draft.Id)!.Status);
    }

    [Fact]
    public async Task Sweeping_twice_closes_nothing_the_second_time()
    {
        // It runs every few seconds forever; a second pass must be a no-op rather than a second
        // broadcast to a room that has already moved on.
        var h = Build();
        var published = await PublishedAsync(h);
        h.Clock.UtcNow = published.ClosesAtUtc!.Value.AddSeconds(30);

        Assert.Equal(1, await h.Sweeper.SweepAsync());
        Assert.Equal(0, await h.Sweeper.SweepAsync());
        Assert.Single(h.Notifier.Notifications.Where(n => n.State == "Closed"));
    }
}
