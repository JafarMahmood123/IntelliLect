using ClassroomService.Application.DTOs.Membership;

namespace ClassroomService.Application.Abstractions;

public interface IMembershipService
{
    Task EnrollStudentAsync(Guid classroomId, Guid studentId, CancellationToken ct = default);
    Task RemoveStudentAsync(Guid classroomId, Guid teacherId, Guid studentId, CancellationToken ct = default);
    /// <summary>The roster, for this classroom's own members. 404 unknown classroom, 403 non-member.</summary>
    Task<IEnumerable<MemberResponse>> GetClassroomMembersAsync(
        Guid classroomId, Guid requestingUserId, CancellationToken ct = default);
}