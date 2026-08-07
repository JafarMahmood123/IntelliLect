using ClassroomService.Application.DTOs.Membership;

namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Super-admin management of a classroom's members (list/add/remove students), bypassing the normal
/// join flow and its teacher-ownership authorization. Name/email enrichment happens in
/// UserManagementService; this service deals only in ids + join dates.
/// </summary>
public interface IClassroomMemberAdminService
{
    /// <exception cref="KeyNotFoundException">The classroom does not exist (5أ).</exception>
    Task<ClassroomMembersResult> GetMembersAsync(Guid classroomId, CancellationToken ct = default);

    /// <summary>
    /// Whether one user belongs to one classroom — the roster question asked one person at a time,
    /// for a service that holds no roster (test-plan G-02). Returns null for an unknown classroom
    /// rather than throwing, because the caller's only sensible response to either is to refuse.
    /// </summary>
    Task<ClassroomAccessResult?> GetAccessAsync(
        Guid classroomId, Guid userId, CancellationToken ct = default);

    /// <summary>Adds a student. A no-op (Changed=false) when the student already belongs (5ج).</summary>
    /// <exception cref="KeyNotFoundException">The classroom does not exist (5أ).</exception>
    Task<MemberMutationResult> AddMemberAsync(Guid classroomId, Guid studentId, CancellationToken ct = default);

    /// <exception cref="KeyNotFoundException">The classroom (5أ) or the membership (5د) does not exist.</exception>
    /// <exception cref="Exceptions.ConflictException">The target is the classroom teacher (5هـ).</exception>
    Task<MemberMutationResult> RemoveMemberAsync(Guid classroomId, Guid studentId, CancellationToken ct = default);
}
