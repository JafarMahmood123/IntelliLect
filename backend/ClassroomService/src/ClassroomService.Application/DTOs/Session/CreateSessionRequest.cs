using System.ComponentModel.DataAnnotations;
using ClassroomService.Domain.Enums;

namespace ClassroomService.Application.DTOs.Session;

public class CreateSessionRequest
{
    [Required]
    [StringLength(100)]
    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    [Required]
    public DateTime ScheduledAtUtc { get; set; }
    public StudentParticipationMode ParticipationMode { get; set; }

    /// <summary>
    /// Whether to start recording this session. Opt-in — an omitted value means no recording, so a
    /// session is never captured by accident. The teacher can still start recording later from
    /// inside the session; stopping it there is final.
    /// </summary>
    public bool RecordingEnabled { get; set; }
}