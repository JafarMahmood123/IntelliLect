using ClassroomService.Application.Abstractions;
using ClassroomService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassroomService.Infrastructure.Persistence.Repositories;

public sealed class SessionRepository : GenericRepository<LearningSession>, ISessionRepository
{
    public SessionRepository(ApplicationDbContext context) : base(context) { }

    public async Task<IEnumerable<LearningSession>> GetSessionsByClassroomAsync(Guid classroomId)
    {
        return await _context.Set<LearningSession>()
            .Where(s => s.ClassroomId == classroomId)
            .OrderByDescending(s => s.ScheduledAtUtc)
            .ToListAsync();
    }
}
