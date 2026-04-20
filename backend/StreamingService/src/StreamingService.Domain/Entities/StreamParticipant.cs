namespace StreamingService.Domain.Entities;

public sealed class StreamParticipant
{
    public Guid Id { get; set; }
    public Guid StreamId { get; set; }
    public Guid UserId { get; set; }
    public bool IsHandRaised { get; set; }
    public DateTime JoinedAtUtc { get; set; }

    // Navigation
    public LiveStream Stream { get; set; } = null!;
}