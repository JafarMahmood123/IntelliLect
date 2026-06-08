namespace StreamingService.Domain.Entities;

public sealed class StreamReaction
{
    public Guid Id { get; set; }
    public Guid StreamId { get; set; }
    public Guid UserId { get; set; }
    public string Emoji { get; set; } = null!;
    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;

    // Navigation
    public LiveStream Stream { get; set; } = null!;
}