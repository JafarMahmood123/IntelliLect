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
}

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
