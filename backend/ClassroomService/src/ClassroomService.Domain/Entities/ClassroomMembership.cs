namespace ClassroomService.Domain.Entities;

public sealed class ClassroomMembership
{
    public Guid Id { get; set; }
    public Guid ClassroomId { get; set; }
    public Guid StudentId { get; set; }
    public DateTime JoinedAtUtc { get; set; }

    // Navigation
    public Classroom Classroom { get; set; } = null!;
}