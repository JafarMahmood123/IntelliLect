using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Quiz;
using ClassroomService.Application.Exceptions;
using ClassroomService.Application.Services;
using ClassroomService.Domain.Entities;

namespace ClassroomService.UnitTests;

/// <summary>
/// Classroom-wide tracking: the cumulative view across every session, which no amount of reading
/// one session at a time gives you.
///
/// The rules worth protecting are the ones that decide what a percentage MEANS — that it is
/// measured against what the class was offered rather than what a student turned up for, that a
/// cancelled or draft quiz never inflates a total, and that a student's own view still names
/// nobody else.
/// </summary>
public sealed class QuizTrackingTests
{
    private static readonly Guid ClassroomId = Guid.NewGuid();
    private static readonly Guid OtherClassroomId = Guid.NewGuid();
    private static readonly Guid TeacherId = Guid.NewGuid();
    private static readonly Guid StudentId = Guid.NewGuid();
    private static readonly Guid SecondStudentId = Guid.NewGuid();

    private sealed record Harness(
        QuizService Service, FakeQuizRepository Quizzes, FakeClock Clock, List<Session> Sessions);

    private sealed class NoAssistant : ILiveAssistantInternalClient
    {
        public Task<GeneratedQuizDto> GenerateQuizAsync(
            Guid sessionId, Guid classroomId, int questionCount, int minOptions, int maxOptions,
            IReadOnlyList<string>? avoid = null, bool wholeSession = false,
            CancellationToken ct = default)
            => throw new NotSupportedException("Tracking does not generate quizzes.");
        public Task<GeneratedQuestionDto> GenerateAnswersAsync(
            Guid sessionId, Guid classroomId, string questionText, int minOptions, int maxOptions,
            CancellationToken ct = default)
            => throw new NotSupportedException("Tracking does not generate answers.");
        public Task<int?> GetTranscriptSegmentCountAsync(Guid sessionId, CancellationToken ct = default)
            => Task.FromResult<int?>(0);
        public Task DeleteSessionTranscriptAsync(Guid sessionId, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<int> DeleteClassroomTranscriptsAsync(Guid classroomId, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private static Harness Build(int sessionCount = 2)
    {
        var classrooms = new FakeClassroomRepository();
        classrooms.Seed(new Classroom { Id = ClassroomId, TeacherId = TeacherId, Name = "Physics" });

        var members = new FakeMembershipRepository();
        members.Enroll(ClassroomId, StudentId);
        members.Enroll(ClassroomId, SecondStudentId);

        var sessions = Enumerable.Range(0, sessionCount)
            .Select(i => new Session
            {
                Id = Guid.NewGuid(),
                ClassroomId = ClassroomId,
                Title = $"Lesson {i + 1}",
                ScheduledAtUtc = new DateTime(2026, 1, i + 1, 9, 0, 0, DateTimeKind.Utc),
            })
            .ToList();

        var quizzes = new FakeQuizRepository();
        var service = new QuizService(
            quizzes, classrooms, members, new FakeSessionRepository([.. sessions]),
            new RecordingQuizNotifier(), new NoAssistant(), new FakeUnitOfWork(), new FakeClock(),
            new FakeQuizSettings(), new RecordingLogger<QuizService>());

        return new Harness(service, quizzes, new FakeClock(), sessions);
    }

    private static QuizDraftRequest Draft(int questions = 1, int points = 5)
        => new("Check", Enumerable.Range(0, questions).Select(q =>
            new QuestionDraftRequest($"Question {q}", points, 60, [
                new OptionDraftRequest("Right", true),
                new OptionDraftRequest("Wrong", false),
            ])).ToList());

    /// <summary>Publishes a quiz in the given session and returns the teacher's view of it.</summary>
    private static async Task<QuizTeacherDto> PublishAsync(Harness h, Guid sessionId, int points = 5)
    {
        var draft = await h.Service.CreateDraftAsync(
            ClassroomId, sessionId, TeacherId, Draft(points: points), default);
        return await h.Service.PublishAsync(ClassroomId, draft.Id, TeacherId, default);
    }

    /// <summary>Closes a quiz, which is what makes its marks visible to a student.</summary>
    private static Task CloseAsync(Harness h, QuizTeacherDto quiz)
        => h.Service.CloseAsync(ClassroomId, quiz.Id, TeacherId, default);

    private static async Task AnswerAsync(
        Harness h, QuizTeacherDto quiz, Guid studentId, string name, bool correctly)
    {
        var view = await h.Service.GetForStudentAsync(ClassroomId, quiz.Id, studentId, default);
        var question = quiz.Questions[0];
        var option = correctly
            ? question.Options.First(o => o.IsCorrect)
            : question.Options.First(o => !o.IsCorrect);
        await h.Service.SubmitAnswerAsync(
            ClassroomId, quiz.Id, studentId, name,
            new SubmitAnswerRequest(view.Questions[0].Id, option.Id), default);
    }

    [Fact]
    public async Task The_teacher_sees_the_class_size_the_quiz_count_and_every_cumulative_score()
    {
        var h = Build();
        var first = await PublishAsync(h, h.Sessions[0].Id);
        var second = await PublishAsync(h, h.Sessions[1].Id);
        await AnswerAsync(h, first, StudentId, "Amina", correctly: true);
        await AnswerAsync(h, second, StudentId, "Amina", correctly: true);
        await AnswerAsync(h, first, SecondStudentId, "Bilal", correctly: false);

        var tracking = await h.Service.GetClassroomTrackingAsync(ClassroomId, TeacherId, default);

        Assert.Equal(2, tracking.EnrolledStudentCount);
        Assert.Equal(2, tracking.ActiveStudentCount);
        Assert.Equal(2, tracking.QuizCount);
        Assert.Equal(10, tracking.TotalPointsAvailable);

        var amina = tracking.Students.Single(s => s.StudentName == "Amina");
        Assert.Equal(10, amina.Score);
        Assert.Equal(100, amina.Percentage);
        Assert.Equal(2, amina.QuizzesTaken);
        Assert.Equal(2, amina.SessionsTakenPart);

        var bilal = tracking.Students.Single(s => s.StudentName == "Bilal");
        Assert.Equal(0, bilal.Score);
        Assert.Equal(1, bilal.QuizzesTaken);
    }

    [Fact]
    public async Task A_percentage_is_measured_against_what_the_class_was_offered()
    {
        // Not against what the student turned up for. Measuring against only the quizzes they sat
        // would give a student who missed the hard week a better score for having missed it, which
        // is the opposite of what a tracking view is for.
        var h = Build();
        var first = await PublishAsync(h, h.Sessions[0].Id);
        await PublishAsync(h, h.Sessions[1].Id);
        await AnswerAsync(h, first, StudentId, "Amina", correctly: true);

        var tracking = await h.Service.GetClassroomTrackingAsync(ClassroomId, TeacherId, default);

        var amina = Assert.Single(tracking.Students);
        Assert.Equal(5, amina.Score);
        Assert.Equal(10, amina.TotalPointsAvailable);
        Assert.Equal(50, amina.Percentage);
        Assert.Equal(1, amina.QuizzesTaken);
        Assert.Equal(2, amina.QuizCount);
    }

    [Fact]
    public async Task A_cancelled_quiz_does_not_inflate_the_total_available()
    {
        var h = Build();
        var first = await PublishAsync(h, h.Sessions[0].Id);
        var second = await PublishAsync(h, h.Sessions[1].Id);
        await AnswerAsync(h, first, StudentId, "Amina", correctly: true);
        await h.Service.CancelAsync(ClassroomId, second.Id, TeacherId, default);

        var tracking = await h.Service.GetClassroomTrackingAsync(ClassroomId, TeacherId, default);

        Assert.Equal(1, tracking.QuizCount);
        Assert.Equal(5, tracking.TotalPointsAvailable);
        Assert.Equal(100, Assert.Single(tracking.Students).Percentage);
    }

    [Fact]
    public async Task A_draft_nobody_ever_saw_is_not_tracked()
    {
        var h = Build();
        await h.Service.CreateDraftAsync(ClassroomId, h.Sessions[0].Id, TeacherId, Draft(), default);

        var tracking = await h.Service.GetClassroomTrackingAsync(ClassroomId, TeacherId, default);

        Assert.Equal(0, tracking.QuizCount);
        Assert.Equal(0, tracking.TotalPointsAvailable);
        Assert.Empty(tracking.Sessions);
    }

    [Fact]
    public async Task A_student_who_finished_without_answering_still_counts_as_having_taken_it()
    {
        // A fact about their term worth keeping, not one to round away.
        var h = Build();
        var quiz = await PublishAsync(h, h.Sessions[0].Id);
        await h.Service.SubmitQuizAsync(ClassroomId, quiz.Id, StudentId, "Amina", default);

        var tracking = await h.Service.GetClassroomTrackingAsync(ClassroomId, TeacherId, default);

        var amina = Assert.Single(tracking.Students);
        Assert.Equal(1, amina.QuizzesTaken);
        Assert.Equal(0, amina.AnsweredCount);
        Assert.Equal(0, amina.Score);
    }

    [Fact]
    public async Task The_class_average_ignores_students_who_have_never_taken_part()
    {
        // Counting an absent student as a zero would report a failing class when what it has is an
        // absent one — two very different problems.
        var h = Build();
        var quiz = await PublishAsync(h, h.Sessions[0].Id);
        await AnswerAsync(h, quiz, StudentId, "Amina", correctly: true);

        var tracking = await h.Service.GetClassroomTrackingAsync(ClassroomId, TeacherId, default);

        Assert.Equal(2, tracking.EnrolledStudentCount);
        Assert.Equal(1, tracking.ActiveStudentCount);
        Assert.Equal(100, tracking.ClassAveragePercentage);
    }

    [Fact]
    public async Task Sessions_are_listed_newest_first_with_how_that_lesson_went()
    {
        var h = Build();
        var first = await PublishAsync(h, h.Sessions[0].Id);
        var second = await PublishAsync(h, h.Sessions[1].Id);
        await AnswerAsync(h, first, StudentId, "Amina", correctly: true);
        await AnswerAsync(h, second, StudentId, "Amina", correctly: false);

        var tracking = await h.Service.GetClassroomTrackingAsync(ClassroomId, TeacherId, default);

        Assert.Equal(2, tracking.Sessions.Count);
        Assert.Equal(h.Sessions[1].Id, tracking.Sessions[0].SessionId);  // newest first
        Assert.Equal(0, tracking.Sessions[0].AveragePercentage);
        Assert.Equal(100, tracking.Sessions[1].AveragePercentage);
        Assert.Equal(1, tracking.Sessions[0].ParticipantCount);
    }

    [Fact]
    public async Task Another_classrooms_quizzes_are_never_counted()
    {
        var h = Build();
        var quiz = await PublishAsync(h, h.Sessions[0].Id);
        await AnswerAsync(h, quiz, StudentId, "Amina", correctly: true);
        // A quiz belonging to a different classroom, reachable through the same repository.
        h.Quizzes.Seed(new Quiz
        {
            Id = Guid.NewGuid(),
            ClassroomId = OtherClassroomId,
            SessionId = Guid.NewGuid(),
            Status = Domain.Enums.QuizStatus.Closed,
            Questions = [],
        });

        var tracking = await h.Service.GetClassroomTrackingAsync(ClassroomId, TeacherId, default);

        Assert.Equal(1, tracking.QuizCount);
    }

    // --- the student's own view ------------------------------------------------------

    [Fact]
    public async Task A_student_sees_their_own_total_and_a_row_per_session()
    {
        var h = Build();
        var first = await PublishAsync(h, h.Sessions[0].Id);
        var second = await PublishAsync(h, h.Sessions[1].Id);
        await AnswerAsync(h, first, StudentId, "Amina", correctly: true);
        await AnswerAsync(h, second, StudentId, "Amina", correctly: false);
        await CloseAsync(h, first);
        await CloseAsync(h, second);

        var mine = await h.Service.GetMyClassroomTrackingAsync(ClassroomId, StudentId, default);

        Assert.Equal(5, mine.Score);
        Assert.Equal(10, mine.TotalPointsAvailable);
        Assert.Equal(50, mine.Percentage);
        Assert.Equal(2, mine.QuizzesTaken);
        Assert.Equal(2, mine.Sessions.Count);
        Assert.Equal(h.Sessions[1].Id, mine.Sessions[0].SessionId);  // newest first
    }

    [Fact]
    public async Task An_open_quiz_does_not_move_a_students_own_total()
    {
        // The leak this guards against is arithmetic, not a field. PointsAwarded is written when
        // the answer is written, so a total that includes an open quiz jumps by the question's
        // marks when the student answers correctly and stays put when they do not — telling them
        // which it was while their answer is still changeable. That is exactly what GetMyResult
        // and the submit acknowledgement withhold on purpose.
        var h = Build();
        var open = await PublishAsync(h, h.Sessions[0].Id);

        var before = await h.Service.GetMyClassroomTrackingAsync(ClassroomId, StudentId, default);
        await AnswerAsync(h, open, StudentId, "Amina", correctly: true);
        var after = await h.Service.GetMyClassroomTrackingAsync(ClassroomId, StudentId, default);

        Assert.Equal(before.Score, after.Score);
        Assert.Equal(before.Percentage, after.Percentage);
        // Not merely zeroed: an open quiz is not part of the student's graded record at all, so
        // its marks are not on offer yet either.
        Assert.Equal(0, after.TotalPointsAvailable);
        Assert.Empty(after.Sessions);
    }

    [Fact]
    public async Task Closing_the_quiz_is_what_releases_it_into_the_students_record()
    {
        var h = Build();
        var quiz = await PublishAsync(h, h.Sessions[0].Id);
        await AnswerAsync(h, quiz, StudentId, "Amina", correctly: true);

        await CloseAsync(h, quiz);
        var mine = await h.Service.GetMyClassroomTrackingAsync(ClassroomId, StudentId, default);

        Assert.Equal(5, mine.Score);
        Assert.Equal(5, mine.TotalPointsAvailable);
        Assert.Equal(100, mine.Percentage);
        Assert.Single(mine.Sessions);
    }

    [Fact]
    public async Task The_class_average_a_student_is_shown_also_excludes_open_quizzes()
    {
        // Otherwise the same inference works through the average: in a quiz only this student has
        // answered so far, the class average IS their score.
        var h = Build();
        var open = await PublishAsync(h, h.Sessions[0].Id);
        await AnswerAsync(h, open, StudentId, "Amina", correctly: true);

        var mine = await h.Service.GetMyClassroomTrackingAsync(ClassroomId, StudentId, default);

        Assert.Equal(0, mine.ClassAveragePercentage);
    }

    [Fact]
    public async Task The_teacher_still_sees_an_open_quiz_filling_in()
    {
        // The narrowing is student-facing only. Watching a live quiz is the teacher's whole job,
        // so the two views disagree while a quiz is open — deliberately, because they answer
        // different questions.
        var h = Build();
        var open = await PublishAsync(h, h.Sessions[0].Id);
        await AnswerAsync(h, open, StudentId, "Amina", correctly: true);

        var tracking = await h.Service.GetClassroomTrackingAsync(ClassroomId, TeacherId, default);

        Assert.Equal(5, tracking.TotalPointsAvailable);
        Assert.Equal(5, tracking.Students.Single(s => s.StudentName == "Amina").Score);
    }

    [Fact]
    public async Task A_students_view_names_nobody_else()
    {
        // The class average is the only thing said about anyone else, and it is one number.
        var h = Build();
        var quiz = await PublishAsync(h, h.Sessions[0].Id);
        await AnswerAsync(h, quiz, StudentId, "Amina", correctly: true);
        await AnswerAsync(h, quiz, SecondStudentId, "Bilal", correctly: false);
        await CloseAsync(h, quiz);

        var mine = await h.Service.GetMyClassroomTrackingAsync(ClassroomId, StudentId, default);

        Assert.Equal(100, mine.Percentage);
        Assert.Equal(50, mine.ClassAveragePercentage);
        Assert.DoesNotContain("Bilal", System.Text.Json.JsonSerializer.Serialize(mine));
    }

    [Fact]
    public async Task A_session_a_student_missed_is_still_listed_as_a_zero()
    {
        // Hiding it would leave them wondering why their percentage is lower than their rows say.
        var h = Build();
        var first = await PublishAsync(h, h.Sessions[0].Id);
        var second = await PublishAsync(h, h.Sessions[1].Id);
        await AnswerAsync(h, first, StudentId, "Amina", correctly: true);
        await CloseAsync(h, first);
        await CloseAsync(h, second);

        var mine = await h.Service.GetMyClassroomTrackingAsync(ClassroomId, StudentId, default);

        var missed = mine.Sessions.Single(s => s.SessionId == h.Sessions[1].Id);
        Assert.Equal(0, missed.QuizzesTaken);
        Assert.Equal(0, missed.Score);
        Assert.Equal(5, missed.TotalPoints);
        Assert.Equal(1, mine.SessionsTakenPart);
        Assert.Equal(2, mine.SessionsWithQuizzesCount);
    }

    [Fact]
    public async Task A_student_cannot_read_the_teachers_tracking()
    {
        var h = Build();

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => h.Service.GetClassroomTrackingAsync(ClassroomId, StudentId, default));
    }

    [Fact]
    public async Task A_classroom_with_no_quizzes_reports_zeroes_rather_than_failing()
    {
        var h = Build();

        var tracking = await h.Service.GetClassroomTrackingAsync(ClassroomId, TeacherId, default);
        var mine = await h.Service.GetMyClassroomTrackingAsync(ClassroomId, StudentId, default);

        Assert.Equal(0, tracking.QuizCount);
        Assert.Equal(0, tracking.ClassAveragePercentage);
        Assert.Empty(tracking.Students);
        Assert.Equal(0, mine.Percentage);
        Assert.Empty(mine.Sessions);
    }
}
