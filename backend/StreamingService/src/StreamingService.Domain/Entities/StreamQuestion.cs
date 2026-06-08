namespace StreamingService.Domain.Entities;

public sealed class StreamQuestion
{
    public Guid Id { get; set; }
    public Guid StreamId { get; set; }
    public Guid UserId { get; set; }
    public string UserName { get; set; } = null!;
    public string QuestionText { get; set; } = null!;
    public string? AnswerText { get; set; }
    public bool IsAnswered { get; set; }
    public DateTime AskedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? AnsweredAtUtc { get; set; }

    // Navigation
    public LiveStream Stream { get; set; } = null!;
}