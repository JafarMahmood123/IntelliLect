namespace ClassroomService.Domain.Entities;

public sealed class Classroom
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string Description { get; set; } = null!;
    public Guid TeacherId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public ICollection<ClassroomFile> Files { get; set; } = new List<ClassroomFile>();
}