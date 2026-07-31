namespace ClassroomService.Domain.Entities;

/// <summary>
/// One selectable choice. Exactly one option per question carries <see cref="IsCorrect"/>.
///
/// NOTE: this entity must never be projected to a student. It is the reason the read models are
/// split in two — a DTO that carries IsCorrect to the browser makes the whole quiz pointless,
/// and it is visible in devtools long before anyone notices.
/// </summary>
public sealed class QuizAnswerOption
{
    public Guid Id { get; set; }
    public Guid QuestionId { get; set; }
    public int Order { get; set; }
    public string Text { get; set; } = null!;
    public bool IsCorrect { get; set; }

    public QuizQuestion Question { get; set; } = null!;
}
