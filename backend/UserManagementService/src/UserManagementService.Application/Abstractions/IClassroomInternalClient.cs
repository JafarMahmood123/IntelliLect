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

    // --- Teacher reassignment (ownership transfer) ---

    /// <exception cref="Common.NotFoundException">The classroom does not exist (3أ).</exception>
    /// <exception cref="InvalidOperationException">A live session is in progress (3ب) or the version is stale.</exception>
    Task<ClassroomTeacherChange> ChangeClassroomTeacherAsync(
        Guid id, Guid newTeacherId, long version, CancellationToken ct = default);

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

    // --- Session deletion with impact preview ---

    /// <returns>The deletion impact preview (step 3), or null if the session does not exist (5أ).</returns>
    Task<SessionDeletionImpact?> GetSessionDeletionImpactAsync(Guid sessionId, CancellationToken ct = default);

    /// <exception cref="Common.NotFoundException">The session does not exist (5أ).</exception>
    /// <exception cref="InvalidOperationException">The session is currently live (5ب).</exception>
    Task<SessionDeletionResult> DeleteSessionAsync(Guid sessionId, string reason, CancellationToken ct = default);

    // --- Knowledge-base file registry (ClassroomService owns the files) ---

    /// <summary>Authoritative cross-classroom file page (drives the list when no status filter is set).</summary>
    Task<AdminFilePage> GetFilesAsync(
        int page, int pageSize, string? search, Guid? classroomId, CancellationToken ct = default);

    /// <summary>File registry rows for a set of ids (enriches a KnowledgeService-driven filtered list).</summary>
    Task<IReadOnlyList<AdminFile>> GetFilesByIdsAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken ct = default);

    /// <summary>Batch classroom-name resolution for a set of classroom ids.</summary>
    Task<IReadOnlyList<ClassroomName>> GetClassroomNamesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <exception cref="Common.NotFoundException">The file does not exist (7أ).</exception>
    Task<FileDeletionResult> DeleteFileAsync(Guid fileId, string reason, CancellationToken ct = default);

    // --- Session outputs (recordings + summaries), owned by ClassroomService ---

    Task<AdminOutputPage> GetOutputsAsync(
        int page, int pageSize, string? search, string? type, string? status, Guid? classroomId, CancellationToken ct = default);

    /// <exception cref="Common.NotFoundException">The recording does not exist (5أ).</exception>
    /// <exception cref="InvalidOperationException">The recording's session is live (5ب).</exception>
    Task<OutputDeletionResult> DeleteRecordingAsync(Guid recordingId, string reason, CancellationToken ct = default);

    /// <exception cref="Common.NotFoundException">The summary does not exist (5أ).</exception>
    /// <exception cref="InvalidOperationException">The summary's session is live (5ب).</exception>
    Task<OutputDeletionResult> DeleteSummaryAsync(Guid summaryId, string reason, CancellationToken ct = default);
}

/// <summary>A session output row (mirrors ClassroomService's AdminOutputRow).</summary>
public sealed record AdminOutput(
    Guid OutputId,
    string Type,
    Guid SessionId,
    string SessionTitle,
    Guid ClassroomId,
    string ClassName,
    string Status,
    long SizeBytes,
    DateTime CreatedAtUtc);

public sealed record AdminOutputPage(
    IReadOnlyList<AdminOutput> Items,
    int TotalCount,
    int PageNumber,
    int PageSize);

public sealed record OutputDeletionResult(Guid OutputId, string Type, bool StorageDeleted, bool RowDeleted);

/// <summary>A file registry row (mirrors ClassroomService's AdminFileRow).</summary>
public sealed record AdminFile(
    Guid FileId,
    string FileName,
    string ContentType,
    long SizeBytes,
    Guid ClassroomId);

public sealed record AdminFilePage(
    IReadOnlyList<AdminFile> Items,
    int TotalCount,
    int PageNumber,
    int PageSize);

public sealed record ClassroomName(Guid Id, string Name);

public sealed record FileDeletionResult(Guid FileId, bool StorageDeleted, bool DeIndexed);

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

/// <summary>Session deletion impact preview (mirrors ClassroomService's SessionDeletionImpact).</summary>
public sealed record SessionDeletionImpact(
    Guid SessionId,
    string Title,
    string Status,
    bool HasRecording,
    bool HasSummary,
    bool HasTranscript,
    long StorageBytes,
    bool IsLive,
    bool TranscriptUnavailable);

/// <summary>Outcome of a completed session deletion (mirrors ClassroomService's SessionDeletionResult).</summary>
public sealed record SessionDeletionResult(
    Guid SessionId,
    bool RecordingDeleted,
    bool SummaryDeleted,
    bool TranscriptDeleted);

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

/// <summary>Outcome of an ownership transfer (mirrors ClassroomService's ChangeTeacherResult).
/// <see cref="Changed"/> is false for the 4ب no-op (the new teacher already owned it).</summary>
public sealed record ClassroomTeacherChange(
    bool Changed,
    Guid PreviousTeacherId,
    Guid NewTeacherId,
    string ClassroomName);

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
