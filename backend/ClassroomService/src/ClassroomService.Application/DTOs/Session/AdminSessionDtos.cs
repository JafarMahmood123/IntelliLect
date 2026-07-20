namespace ClassroomService.Application.DTOs.Session;

/// <summary>
/// A session as seen in the super-admin monitor: core fields joined with its classroom and the
/// recording/summary status. Teacher identity is a bare id — the name is resolved by the caller
/// (UserManagementService), which owns user data.
/// </summary>
public sealed record AdminSessionResponse
{
    public Guid SessionId { get; init; }
    public Guid ClassroomId { get; init; }
    public string ClassName { get; init; } = string.Empty;
    public Guid TeacherId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime ScheduledAtUtc { get; init; }
    public DateTime? StartedAtUtc { get; init; }
    public DateTime? EndedAtUtc { get; init; }
    public string? RecordingStatus { get; init; }
    public string? SummaryStatus { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}

/// <summary>Outcome of a force-end, recording the result of each best-effort step (step 8 / 7أ).</summary>
public sealed record ForceEndSessionResponse(
    Guid SessionId,
    string Status,
    bool AlreadyEnded,
    bool StreamEnded,
    bool SummaryTriggered);
