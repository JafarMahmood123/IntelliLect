using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Abstractions;

public interface IClassroomRepository : IRepository<Classroom>
{
    Task<Classroom?> GetWithDetailsAsync(Guid id, CancellationToken ct = default);
    Task<List<Classroom>> GetByTeacherIdAsync(Guid teacherId, CancellationToken ct = default);
    Task<List<Classroom>> GetEnrolledClassroomsAsync(Guid studentId, CancellationToken ct = default);
}