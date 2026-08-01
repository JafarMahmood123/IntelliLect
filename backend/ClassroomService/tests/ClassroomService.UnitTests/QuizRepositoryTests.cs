using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
using ClassroomService.Infrastructure.Persistence;
using ClassroomService.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ClassroomService.UnitTests;

/// <summary>
/// Repository tests against a REAL EF context (SQLite in memory), not the in-memory fake the
/// service tests use.
///
/// They exist because of a bug the fake could never have caught. The repository used to sort a
/// loaded quiz by reassigning its navigations —
/// <c>quiz.Questions = quiz.Questions.OrderBy(...).ToList()</c> — which replaced EF's own tracked
/// collections with plain lists and broke change tracking on the graph. Every read still returned
/// the right data, so it looked correct; the damage only appeared when something later WROTE to
/// that graph, and editing a draft failed with a DbUpdateConcurrencyException.
///
/// A fake repository holding a dictionary has no change tracker, so no test built on one can see
/// this class of fault. That is what these cover: the repository's contract with EF, not the
/// service's logic.
/// </summary>
public sealed class QuizRepositoryTests : IDisposable
{
    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly Guid ClassroomId = Guid.NewGuid();

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;

    public QuizRepositoryTests()
    {
        // A shared in-memory database lives as long as the connection is open, so several contexts
        // can see the same data — needed to load in one context and write in another, the way a
        // request does.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new ApplicationDbContext(_options);
        context.Database.EnsureCreated();
        // A quiz cascades from its session, so the session row has to exist first.
        context.Sessions.Add(new Session
        {
            Id = SessionId,
            ClassroomId = ClassroomId,
            Title = "Week 1",
            Status = SessionStatus.Live,
        });
        context.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Replacing_a_drafts_questions_saves_cleanly()
    {
        // The exact shape of the failure: load a draft, delete its questions, add new ones, save.
        // With the navigations swapped out from under EF this threw DbUpdateConcurrencyException.
        var quizId = await SeedDraftAsync(questionCount: 2, optionsPerQuestion: 3);

        await using (var context = new ApplicationDbContext(_options))
        {
            var repository = new QuizRepository(context);
            var quiz = await repository.GetWithQuestionsAsync(quizId);
            Assert.NotNull(quiz);

            repository.RemoveQuestions(quiz!.Questions.ToList());
            quiz.Questions.Clear();
            quiz.Questions.Add(NewQuestion(quizId, order: 0, "A replacement question"));

            await context.SaveChangesAsync();
        }

        await using var verify = new ApplicationDbContext(_options);
        var reloaded = await new QuizRepository(verify).GetWithQuestionsAsync(quizId);

        Assert.NotNull(reloaded);
        Assert.Single(reloaded!.Questions);
        Assert.Equal("A replacement question", reloaded.Questions.Single().Text);
        // The old questions and their options went with them, rather than being orphaned.
        Assert.Equal(1, await verify.QuizQuestions.CountAsync());
        Assert.Equal(2, await verify.QuizAnswerOptions.CountAsync());
    }

    [Fact]
    public async Task Loading_a_quiz_returns_questions_and_options_in_their_declared_order()
    {
        // Ordering still matters: rows come back in whatever order the database likes, and a
        // student's options shuffling between page loads would be its own bug.
        var quizId = Guid.NewGuid();
        await using (var seed = new ApplicationDbContext(_options))
        {
            var quiz = NewQuiz(quizId);
            // Added deliberately out of order, so passing cannot be an accident of insertion order.
            foreach (var order in new[] { 2, 0, 1 })
            {
                var question = NewQuestion(quizId, order, $"Question {order}");
                question.Options.Clear();
                foreach (var optionOrder in new[] { 1, 0 })
                {
                    question.Options.Add(new QuizAnswerOption
                    {
                        Id = Guid.NewGuid(),
                        QuestionId = question.Id,
                        Order = optionOrder,
                        Text = $"Option {optionOrder}",
                        IsCorrect = optionOrder == 0,
                    });
                }
                quiz.Questions.Add(question);
            }
            seed.Quizzes.Add(quiz);
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(_options);
        var loaded = await new QuizRepository(context).GetWithQuestionsAsync(quizId);

        Assert.NotNull(loaded);
        Assert.Equal([0, 1, 2], loaded!.Questions.Select(q => q.Order));
        Assert.All(loaded.Questions, q => Assert.Equal([0, 1], q.Options.Select(o => o.Order)));
    }

    [Fact]
    public async Task A_quiz_loaded_for_reading_can_still_be_written_to()
    {
        // The general form of the regression: loading through the repository must leave the graph
        // in a state EF can save. Anything that sorts by swapping collections breaks this.
        var quizId = await SeedDraftAsync(questionCount: 1, optionsPerQuestion: 2);

        await using (var context = new ApplicationDbContext(_options))
        {
            var repository = new QuizRepository(context);
            var quiz = await repository.GetWithQuestionsAsync(quizId);
            quiz!.Status = QuizStatus.Open;
            quiz.Questions.First().Options.Add(new QuizAnswerOption
            {
                Id = Guid.NewGuid(),
                QuestionId = quiz.Questions.First().Id,
                Order = 2,
                Text = "An added option",
                IsCorrect = false,
            });

            await context.SaveChangesAsync();
        }

        await using var verify = new ApplicationDbContext(_options);
        var reloaded = await new QuizRepository(verify).GetWithQuestionsAsync(quizId);

        Assert.Equal(QuizStatus.Open, reloaded!.Status);
        Assert.Equal(3, reloaded.Questions.First().Options.Count);
    }

    // --- helpers ---------------------------------------------------------------

    private async Task<Guid> SeedDraftAsync(int questionCount, int optionsPerQuestion)
    {
        var quizId = Guid.NewGuid();
        await using var context = new ApplicationDbContext(_options);
        var quiz = NewQuiz(quizId);
        for (var i = 0; i < questionCount; i++)
        {
            var question = NewQuestion(quizId, i, $"Question {i}");
            question.Options.Clear();
            for (var o = 0; o < optionsPerQuestion; o++)
            {
                question.Options.Add(new QuizAnswerOption
                {
                    Id = Guid.NewGuid(),
                    QuestionId = question.Id,
                    Order = o,
                    Text = $"Option {o}",
                    IsCorrect = o == 0,
                });
            }
            quiz.Questions.Add(question);
        }
        context.Quizzes.Add(quiz);
        await context.SaveChangesAsync();
        return quizId;
    }

    private static Quiz NewQuiz(Guid quizId) => new()
    {
        Id = quizId,
        SessionId = SessionId,
        ClassroomId = ClassroomId,
        CreatedByTeacherId = Guid.NewGuid(),
        Title = "Draft",
        Status = QuizStatus.Draft,
        CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static QuizQuestion NewQuestion(Guid quizId, int order, string text)
    {
        var question = new QuizQuestion
        {
            Id = Guid.NewGuid(),
            QuizId = quizId,
            Order = order,
            Text = text,
            Points = 1,
            TimeLimitSeconds = 60,
        };
        question.Options.Add(new QuizAnswerOption
        {
            Id = Guid.NewGuid(),
            QuestionId = question.Id,
            Order = 0,
            Text = "Right",
            IsCorrect = true,
        });
        question.Options.Add(new QuizAnswerOption
        {
            Id = Guid.NewGuid(),
            QuestionId = question.Id,
            Order = 1,
            Text = "Wrong",
            IsCorrect = false,
        });
        return question;
    }
}
