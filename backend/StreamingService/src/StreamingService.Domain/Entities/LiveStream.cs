using StreamingService.Domain.Enums;

namespace StreamingService.Domain.Entities;

public sealed class LiveStream
{
    public Guid Id { get; set; }
    public string? EgressId { get; set; }
    public Guid SessionId { get; set; }
    public Guid ClassroomId { get; set; }
    public Guid TeacherId { get; set; }
    public string StreamKey { get; set; } = null!;
    public StreamStatus Status { get; set; } = StreamStatus.Planned;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public StudentParticipationMode ParticipationMode { get; set; }


    // Navigation
    public ICollection<StreamParticipant> Participants { get; set; } = new List<StreamParticipant>();
    public ICollection<StreamChatMessage> ChatMessages { get; set; } = new List<StreamChatMessage>();
    public ICollection<StreamReaction> Reactions { get; set; } = new List<StreamReaction>();
    public ICollection<StreamQuestion> Questions { get; set; } = new List<StreamQuestion>();
}