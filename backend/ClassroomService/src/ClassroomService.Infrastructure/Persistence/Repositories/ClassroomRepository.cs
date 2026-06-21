using ClassroomService.Application.Abstractions;
using ClassroomService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassroomService.Infrastructure.Persistence.Repositories;

public sealed class ClassroomRepository : GenericRepository<Classroom>, IClassroomRepository
{
    public ClassroomRepository(ApplicationDbContext context) : base(context) { }

    public async Task<Classroom?> GetWithDetailsAsync(Guid id, CancellationToken ct)
    {
        return await _context.Set<Classroom>()
            .Include(c => c.Files)
            .Include(c => c.Memberships)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<List<Classroom>> GetByTeacherIdAsync(Guid teacherId, CancellationToken ct)
    {
        return await _context.Set<Classroom>()
            .Where(c => c.TeacherId == teacherId)
            .Include(c => c.Files)
            .Include(c => c.Memberships)
            .ToListAsync(ct);
    }

    public async Task<List<Classroom>> GetEnrolledClassroomsAsync(Guid studentId, CancellationToken ct)
    {
        var classroomIds = _context.Set<ClassroomMembership>()
            .Where(m => m.StudentId == studentId)
            .Select(m => m.ClassroomId);

        return await _context.Set<Classroom>()
            .Where(c => classroomIds.Contains(c.Id))
            .Include(c => c.Files)
            .Include(c => c.Memberships)
            .ToListAsync(ct);
    }
}