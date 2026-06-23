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

}