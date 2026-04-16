using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Abstractions;

public interface ISessionRepository : IRepository<LearningSession>
{
    Task<IEnumerable<LearningSession>> GetSessionsByClassroomAsync(Guid classroomId);
}
