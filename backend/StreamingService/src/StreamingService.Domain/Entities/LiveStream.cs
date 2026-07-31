using StreamingService.Domain.Enums;

namespace StreamingService.Domain.Entities;

public sealed class LiveStream
{
    public Guid Id { get; set; }

    /// <summary>
    /// The LiveKit egress currently recording this session, or a timestamped placeholder while a
    /// caller is claiming the slot. NOT cleared when recording stops: the egress-ended webhook
    /// correlates back to this stream by egress id, so clearing it would silently lose the
    /// recording. <see cref="RecordingState"/> is what says whether recording is wanted.
    /// </summary>
    public string? EgressId { get; set; }

    /// <summary>
    /// Whether the teacher wants this session recorded. Seeded from the session's creation-time
    /// setting (default off) and changed live from inside the session — the same shape as
    /// <see cref="StudentsCanPublishAudio"/>. Every recording path (room_started, the reconcile
    /// loop, session end) reconciles against this rather than inferring intent from the stream
    /// being live.
    /// </summary>
    public RecordingState RecordingState { get; set; } = RecordingState.Off;

    /// <summary>Set once the egress-complete webhook has been turned into a
    /// SessionRecordingReadyMessage (R-1), so duplicate webhook deliveries don't re-publish.</summary>
    public bool RecordingReadyPublished { get; set; }

    public Guid SessionId { get; set; }
    public Guid ClassroomId { get; set; }
    public Guid TeacherId { get; set; }
    public string StreamKey { get; set; } = null!;
    public StreamStatus Status { get; set; } = StreamStatus.Planned;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }

    /// <summary>The mode the session was created with. Kept as the initial seed for the two
    /// runtime flags below; the teacher's in-session "Session Settings" toggles change those
    /// flags, not this.</summary>
    public StudentParticipationMode ParticipationMode { get; set; }

    /// <summary>Whether students are currently allowed to publish their microphone. Toggled live
    /// by the teacher from inside the session (enforced on connected students via LiveKit
    /// UpdateParticipant, and baked into the join token for late arrivals). Seeded from
    /// <see cref="ParticipationMode"/> when the stream is created.</summary>
    public bool StudentsCanPublishAudio { get; set; }

    /// <summary>Whether students are currently allowed to publish their camera. See
    /// <see cref="StudentsCanPublishAudio"/>.</summary>
    public bool StudentsCanPublishVideo { get; set; }


    // Navigation
    public ICollection<StreamParticipant> Participants { get; set; } = new List<StreamParticipant>();
    public ICollection<StreamChatMessage> ChatMessages { get; set; } = new List<StreamChatMessage>();
    public ICollection<StreamReaction> Reactions { get; set; } = new List<StreamReaction>();
    public ICollection<StreamQuestion> Questions { get; set; } = new List<StreamQuestion>();
}