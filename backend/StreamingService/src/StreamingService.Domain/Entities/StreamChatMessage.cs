namespace StreamingService.Domain.Entities;

public sealed class StreamChatMessage
{
    public Guid Id { get; set; }
    public Guid StreamId { get; set; }
    public Guid UserId { get; set; }
    public string UserName { get; set; } = null!;
    public string Message { get; set; } = null!;
    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;

    // Navigation
    public LiveStream Stream { get; set; } = null!;
}