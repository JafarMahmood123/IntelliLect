using ClassroomService.Domain.Enums;

namespace ClassroomService.Domain.Entities;

public class Session
{
    public Guid Id { get; set; }
    public Guid ClassroomId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ScheduledAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public StudentParticipationMode ParticipationMode { get; set; } = StudentParticipationMode.ViewOnly;

    /// <summary>
    /// Whether this session starts out being recorded. Defaults to false — recording is opt-in, so
    /// nothing is captured unless the teacher asks for it. Only the initial seed: the teacher can
    /// start recording later from inside the session, and stopping it is final. The live state
    /// lives on StreamingService's LiveStream, not here.
    /// </summary>
    public bool RecordingEnabled { get; set; }

    public SessionStatus Status { get; set; } = SessionStatus.Scheduled;
}