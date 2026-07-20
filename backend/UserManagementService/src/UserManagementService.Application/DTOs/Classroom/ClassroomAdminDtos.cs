namespace UserManagementService.Application.DTOs.Classroom;

/// <summary>Query parameters for the super admin classroom listing.</summary>
public sealed class SearchClassroomsRequest
{
    public string? Search { get; set; }
    public Guid? TeacherId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

/// <summary>Body for creating a classroom on behalf of a teacher.</summary>
public sealed record CreateClassroomAdminRequest(Guid TeacherId, string Name, string Description);

/// <summary>Body for editing a classroom; <see cref="Version"/> carries the concurrency token (6أ).</summary>
public sealed record UpdateClassroomAdminRequest(string Name, string Description, long Version);

/// <summary>
/// A classroom row in the super admin listing: ClassroomService's data enriched with the
/// teacher's name/email (resolved locally, since UserManagementService owns user data).
/// </summary>
public sealed record ClassroomAdminItem(
    Guid Id,
    string Name,
    string Description,
    Guid TeacherId,
    string? TeacherName,
    string? TeacherEmail,
    DateTime CreatedAtUtc,
    int FileCount,
    int StudentCount,
    int SessionCount,
    long Version,
    string Status);

/// <summary>Body for deleting a classroom; the reason is mandatory (4أ).</summary>
public sealed record DeleteClassroomAdminRequest(string Reason);

/// <summary>What deleting a classroom will destroy (step 3), returned to the super admin for preview.</summary>
public sealed record ClassroomDeletionImpactResult(
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

/// <summary>Counts of what a completed deletion removed (step 8).</summary>
public sealed record ClassroomDeletionSummary(
    Guid ClassroomId,
    int RecordingsDeleted,
    int SummariesDeleted,
    int FilesDeleted,
    int SessionsDeleted,
    int MembershipsDeleted);
