namespace ClassroomService.Domain.Entities;

public sealed class SessionRecording
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public string S3Key { get; set; } = null!;
    public long SizeBytes { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    // Navigation
    public Session Session { get; set; } = null!;
}