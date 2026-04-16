namespace ClassroomService.Domain.Entities;

public sealed class LearningSession
{
    public Guid Id { get; set; }
    public string Title { get; set; } = null!;
    public string Description { get; set; } = null!;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ScheduledAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Scheduled;

    public Guid ClassroomId { get; set; }
    public Classroom Classroom { get; set; } = null!;
}