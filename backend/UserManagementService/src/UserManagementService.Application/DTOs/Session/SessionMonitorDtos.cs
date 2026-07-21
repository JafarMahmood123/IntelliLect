namespace UserManagementService.Application.DTOs.Session;

/// <summary>Query parameters for the super admin session listing.</summary>
public sealed class SearchSessionsRequest
{
    public string? Search { get; set; }
    public string? Status { get; set; }
    public Guid? ClassroomId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

/// <summary>
/// A session row in the monitor: ClassroomService data enriched with the teacher's name/email
/// (resolved locally, since UserManagementService owns user data).
/// </summary>
public sealed record SessionMonitorItem(
    Guid SessionId,
    Guid ClassroomId,
    string ClassName,
    Guid TeacherId,
    string? TeacherName,
    string? TeacherEmail,
    string Title,
    string Status,
    DateTime ScheduledAtUtc,
    DateTime? StartedAtUtc,
    DateTime? EndedAtUtc,
    string? RecordingStatus,
    string? SummaryStatus);

/// <summary>A live session with its real-time overlay (participants / recording / AI assistant).</summary>
public sealed record LiveSessionItem(
    Guid SessionId,
    Guid ClassroomId,
    string ClassName,
    Guid TeacherId,
    string? TeacherName,
    string Title,
    DateTime? StartedAtUtc,
    int? ParticipantCount,
    bool? IsRecording,
    bool? AssistantRunning);

/// <summary>
/// The live view. <see cref="RealtimeUnavailable"/> is true when the streaming/assistant
/// snapshot could not be fetched — the sessions are still listed from stored data (4أ).
/// </summary>
public sealed record LiveSessionsResponse(
    IReadOnlyList<LiveSessionItem> Items,
    bool RealtimeUnavailable);

/// <summary>Body for a force-end; the reason is mandatory (5أ).</summary>
public sealed record ForceEndSessionRequest(string Reason);

/// <summary>Result of a force-end, reporting each step's outcome (step 8 / 7أ).</summary>
public sealed record ForceEndSessionResult(
    Guid SessionId,
    string Status,
    bool AlreadyEnded,
    bool StreamEnded,
    bool SummaryTriggered);

/// <summary>Body for deleting a session; the reason is mandatory (4أ).</summary>
public sealed record DeleteSessionRequest(string Reason);

/// <summary>What deleting a session will destroy (step 3), returned to the super admin for preview.</summary>
public sealed record SessionDeletionImpactResult(
    Guid SessionId,
    string Title,
    string Status,
    bool HasRecording,
    bool HasSummary,
    bool HasTranscript,
    long StorageBytes,
    bool IsLive,
    bool TranscriptUnavailable);

/// <summary>What a completed session deletion removed (step 8).</summary>
public sealed record SessionDeletionSummary(
    Guid SessionId,
    bool RecordingDeleted,
    bool SummaryDeleted,
    bool TranscriptDeleted);
