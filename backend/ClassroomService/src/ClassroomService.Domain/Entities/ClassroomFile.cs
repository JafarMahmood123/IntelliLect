namespace ClassroomService.Domain.Entities;

public sealed class ClassroomFile
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = null!;
    public string S3Key { get; set; } = null!;
    public string ContentType { get; set; } = null!;
    public long SizeBytes { get; set; }
    public Guid ClassroomId { get; set; }
    public Classroom Classroom { get; set; } = null!;
}