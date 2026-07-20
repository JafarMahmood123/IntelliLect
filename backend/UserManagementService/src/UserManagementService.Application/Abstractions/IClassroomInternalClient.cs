namespace UserManagementService.Application.Abstractions;

/// <summary>
/// Reads a user's classroom memberships from ClassroomService (a separate service).
/// Implementations call ClassroomService's internal endpoint over HTTP. A failure to
/// reach it must surface as an exception so callers can degrade gracefully
/// (use-case alternate path 7ب: show the user without their memberships).
/// </summary>
public interface IClassroomInternalClient
{
    Task<UserClassrooms> GetUserClassroomsAsync(Guid userId, CancellationToken ct = default);

    // --- Super-admin classroom administration (over ClassroomService's internal endpoints) ---
    Task<AdminClassroomPage> GetClassroomsAsync(
        int page, int pageSize, string? search, Guid? teacherId, CancellationToken ct = default);

    Task<AdminClassroom?> GetClassroomByIdAsync(Guid id, CancellationToken ct = default);

    Task<Guid> CreateClassroomAsync(Guid teacherId, string name, string description, CancellationToken ct = default);

    /// <exception cref="Common.NotFoundException">The classroom does not exist (5ج).</exception>
    /// <exception cref="InvalidOperationException">The classroom was modified concurrently (6أ).</exception>
    Task UpdateClassroomAsync(Guid id, string name, string description, long version, CancellationToken ct = default);

    // --- Classroom deletion with impact preview ---

    /// <returns>The deletion impact preview (step 3), or null if the classroom does not exist (5أ).</returns>
    Task<ClassroomDeletionImpact?> GetClassroomDeletionImpactAsync(Guid id, CancellationToken ct = default);

    /// <exception cref="Common.NotFoundException">The classroom does not exist (5أ).</exception>
    /// <exception cref="InvalidOperationException">The classroom has a live session (5ب).</exception>
    Task<ClassroomDeletionResult> DeleteClassroomAsync(Guid id, string reason, CancellationToken ct = default);

    // --- Session monitoring / force-end ---
    Task<AdminSessionPage> GetSessionsAsync(
        int page, int pageSize, string? search, string? status, Guid? classroomId, CancellationToken ct = default);

    /// <exception cref="Common.NotFoundException">The session does not exist (6أ).</exception>
    Task<ForceEndResult> ForceEndSessionAsync(Guid sessionId, string reason, CancellationToken ct = default);
}

/// <summary>A session row for the super-admin monitor (mirrors ClassroomService's AdminSessionResponse).</summary>
public sealed record AdminSession(
    Guid SessionId,
    Guid ClassroomId,
    string ClassName,
    Guid TeacherId,
    string Title,
    string Status,
    DateTime ScheduledAtUtc,
    DateTime? StartedAtUtc,
    DateTime? EndedAtUtc,
    string? RecordingStatus,
    string? SummaryStatus,
    DateTime CreatedAtUtc);

public sealed record AdminSessionPage(
    IReadOnlyList<AdminSession> Items,
    int TotalCount,
    int PageNumber,
    int PageSize,
    int TotalPages);

/// <summary>Outcome of a force-end, including each best-effort step's result (step 8 / 7أ).</summary>
public sealed record ForceEndResult(
    Guid SessionId,
    string Status,
    bool AlreadyEnded,
    bool StreamEnded,
    bool SummaryTriggered);

/// <summary>A classroom as administered by the super admin (mirrors ClassroomService's AdminClassroomResponse).</summary>
public sealed record AdminClassroom(
    Guid Id,
    string Name,
    string Description,
    Guid TeacherId,
    DateTime CreatedAtUtc,
    int FileCount,
    int StudentCount,
    int SessionCount,
    long Version,
    string Status);

/// <summary>Deletion impact preview (mirrors ClassroomService's ClassroomDeletionImpact).</summary>
public sealed record ClassroomDeletionImpact(
    Guid ClassroomId,
    string Name,
    string Status,
    int SessionCount,
    int MemberCount,
    int FileCount,
    int RecordingCount,
    int SummaryCount,
    long StorageBytes,
    bool HasLiveSession);

/// <summary>Outcome of a completed classroom deletion (mirrors ClassroomService's ClassroomDeletionResult).</summary>
public sealed record ClassroomDeletionResult(
    Guid ClassroomId,
    int RecordingsDeleted,
    int SummariesDeleted,
    int FilesDeleted,
    int SessionsDeleted,
    int MembershipsDeleted);

/// <summary>A page of admin classrooms (mirrors ClassroomService's PagedResult).</summary>
public sealed record AdminClassroomPage(
    IReadOnlyList<AdminClassroom> Items,
    int TotalCount,
    int PageNumber,
    int PageSize,
    int TotalPages);

/// <summary>A user's classroom memberships: what they teach and what they are enrolled in.</summary>
public sealed record UserClassrooms(
    IReadOnlyList<ClassroomSummary> Teaching,
    IReadOnlyList<ClassroomSummary> Enrolled)
{
    public static readonly UserClassrooms Empty =
        new(Array.Empty<ClassroomSummary>(), Array.Empty<ClassroomSummary>());
}

/// <summary>A classroom as seen from the user-directory view (mirrors ClassroomService's ClassroomResponse).</summary>
public sealed record ClassroomSummary(
    Guid Id,
    string Name,
    string Description,
    Guid TeacherId,
    DateTime CreatedAtUtc,
    int FileCount,
    int StudentCount);
