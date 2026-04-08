using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Abstractions;

public interface IMembershipRepository : IRepository<ClassroomMembership>
{
    Task<bool> IsEnrolledAsync(Guid classroomId, Guid studentId, CancellationToken ct = default);
    Task<List<ClassroomMembership>> GetMembersWithDetailsAsync(Guid classroomId, CancellationToken ct = default);
    Task<ClassroomMembership?> GetMembershipAsync(Guid classroomId, Guid studentId, CancellationToken ct = default);
}