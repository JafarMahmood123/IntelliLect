using ClassroomService.Domain.Enums;

namespace ClassroomService.Domain.Entities;

public sealed class SessionRecording
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid ClassroomId { get; set; }
    public string EgressId { get; set; } = null!;

    /// <summary>S3 object key of the uploaded MP4. Null until the recording is Available.</summary>
    public string? S3Key { get; set; }

    public RecordingStatus Status { get; set; } = RecordingStatus.Processing;
    public int DurationSeconds { get; set; }
    public long SizeBytes { get; set; }
    public string? ContentType { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Set when the recording becomes Available.</summary>
    public DateTime? AvailableAtUtc { get; set; }

    /// <summary>Populated when the recording ends in Failed.</summary>
    public string? Error { get; set; }

    // Navigation
    public Session Session { get; set; } = null!;
}
