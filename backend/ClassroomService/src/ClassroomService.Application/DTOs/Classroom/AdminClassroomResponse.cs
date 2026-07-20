namespace ClassroomService.Application.DTOs.Classroom;

/// <summary>
/// A classroom as seen by a platform administrator: the core fields plus the counts shown in
/// the super-admin listing, and a concurrency <see cref="Version"/> (Postgres xmin) used to
/// detect concurrent edits. Teacher identity is a bare id here — names are resolved by the
/// caller (UserManagementService), which owns user data.
/// </summary>
public sealed record AdminClassroomResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public Guid TeacherId { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public int FileCount { get; init; }
    public int StudentCount { get; init; }
    public int SessionCount { get; init; }
    public long Version { get; init; }

    /// <summary>
    /// "Active" or "PendingDeletion". A PendingDeletion row is shown so the super admin can see a
    /// deletion in progress (or one that stalled and needs re-running), but it is hidden from every
    /// teacher/student surface.
    /// </summary>
    public string Status { get; init; } = "Active";
}
