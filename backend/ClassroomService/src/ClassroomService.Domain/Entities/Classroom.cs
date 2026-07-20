using ClassroomService.Domain.Enums;

namespace ClassroomService.Domain.Entities;

public sealed class Classroom
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string Description { get; set; } = null!;
    public Guid TeacherId { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Deletion lifecycle. <see cref="ClassroomStatus.PendingDeletion"/> takes the classroom out of
    /// use while its data is being purged and marks it resumable if a purge phase fails (6أ).
    /// </summary>
    public ClassroomStatus Status { get; set; } = ClassroomStatus.Active;

    public ICollection<ClassroomFile> Files { get; set; } = new List<ClassroomFile>();
    public ICollection<ClassroomMembership> Memberships { get; set; } = new List<ClassroomMembership>();
}