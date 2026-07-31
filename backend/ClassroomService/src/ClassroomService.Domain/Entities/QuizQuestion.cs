namespace ClassroomService.Domain.Entities;

public sealed class QuizQuestion
{
    public Guid Id { get; set; }
    public Guid QuizId { get; set; }

    /// <summary>Display position. Explicit because row order from the database is not a guarantee.</summary>
    public int Order { get; set; }

    public string Text { get; set; } = null!;

    /// <summary>Marks awarded for the correct option. Set by the teacher when composing.</summary>
    public int Points { get; set; }

    /// <summary>
    /// How long this question is worth. All questions are published at once, so this is NOT
    /// enforced per question — a student with the whole quiz on screen can spend their time however
    /// they like. It exists to compute the quiz's single deadline (and to show as guidance).
    /// </summary>
    public int TimeLimitSeconds { get; set; }

    public Quiz Quiz { get; set; } = null!;
    public ICollection<QuizAnswerOption> Options { get; set; } = new List<QuizAnswerOption>();
}
