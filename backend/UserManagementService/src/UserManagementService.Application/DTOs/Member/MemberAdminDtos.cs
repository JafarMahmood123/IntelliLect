namespace UserManagementService.Application.DTOs.Member;

/// <summary>Query parameters for the super-admin classroom-member listing.</summary>
public sealed class SearchMembersRequest
{
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

/// <summary>
/// A classroom member row: ClassroomService's membership (or the owning teacher) enriched with the
/// user's name/email, which UserManagementService owns. <see cref="IsTeacher"/> marks the owner,
/// who is shown for context but not removable through member management (5هـ).
/// </summary>
public sealed record ClassroomMemberItem(
    Guid UserId,
    string? Name,
    string? Email,
    string RoleInClass,
    DateTime? JoinedAtUtc,
    bool IsTeacher);

/// <summary>Body for adding a student to a classroom.</summary>
public sealed record AddMemberRequest(Guid StudentId);

/// <summary>Body for removing a member; the reason is mandatory (4أ).</summary>
public sealed record RemoveMemberRequest(string Reason);

/// <summary>Outcome of an add/remove returned to the super admin. <see cref="Changed"/> is false for
/// the 5ج no-op (already a member). <see cref="Action"/> is "Added" or "Removed".</summary>
public sealed record MemberChangeSummary(
    bool Changed,
    Guid ClassroomId,
    string ClassroomName,
    Guid StudentId,
    string Action);
