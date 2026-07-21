namespace ClassroomService.Application.DTOs.Membership;

/// <summary>A student membership row (studentId + join date). Name/email are resolved by
/// UserManagementService, which owns the user store.</summary>
public sealed record ClassroomMemberRow(Guid StudentId, DateTime JoinedAtUtc);

/// <summary>
/// The full membership set of a classroom for the super-admin member view: the owning teacher plus
/// every enrolled student. Returned unpaged (a single classroom's roster is bounded); UMS enriches
/// with names/emails and does the search/paging.
/// </summary>
public sealed record ClassroomMembersResult(
    Guid ClassroomId,
    string ClassroomName,
    Guid TeacherId,
    IReadOnlyList<ClassroomMemberRow> Students);

/// <summary>
/// Outcome of an add/remove. <see cref="Changed"/> is false for the 5ج no-op (already a member).
/// </summary>
public sealed record MemberMutationResult(bool Changed, Guid ClassroomId, string ClassroomName, Guid StudentId);
