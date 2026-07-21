using UserManagementService.Application.Common;
using UserManagementService.Application.DTOs.Member;

namespace UserManagementService.Application.Abstractions;

/// <summary>
/// Super admin management of a classroom's members (use-case "إدارة أعضاء الفصل الدراسي"):
/// list (teacher + students, enriched with names/emails), add a student, remove a member. Orchestrates
/// ClassroomService (owns memberships) and validates/enriches user data locally.
/// </summary>
public interface IClassroomMemberAdminService
{
    /// <returns>A page of members (step 3).</returns>
    /// <exception cref="NotFoundException">The classroom does not exist (5أ).</exception>
    Task<PagedResult<ClassroomMemberItem>> GetMembersAsync(Guid classroomId, SearchMembersRequest request, CancellationToken ct = default);

    /// <exception cref="ArgumentException">The target is not an existing, active student (5ب).</exception>
    /// <exception cref="NotFoundException">The classroom does not exist (5أ).</exception>
    Task<MemberChangeSummary> AddMemberAsync(Guid classroomId, AddMemberRequest request, CancellationToken ct = default);

    /// <exception cref="ArgumentException">The removal reason is missing (4أ).</exception>
    /// <exception cref="NotFoundException">The classroom (5أ) or the membership (5د) does not exist.</exception>
    /// <exception cref="InvalidOperationException">The target is the classroom teacher (5هـ).</exception>
    Task<MemberChangeSummary> RemoveMemberAsync(Guid classroomId, Guid studentId, RemoveMemberRequest request, CancellationToken ct = default);
}
