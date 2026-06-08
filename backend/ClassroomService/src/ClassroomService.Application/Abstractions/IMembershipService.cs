using ClassroomService.Application.DTOs.Membership;

namespace ClassroomService.Application.Abstractions;

public interface IMembershipService
{
    Task EnrollStudentAsync(Guid classroomId, Guid studentId, CancellationToken ct = default);
    Task RemoveStudentAsync(Guid classroomId, Guid teacherId, Guid studentId, CancellationToken ct = default);
    Task<IEnumerable<MemberResponse>> GetClassroomMembersAsync(Guid classroomId, CancellationToken ct = default);
}