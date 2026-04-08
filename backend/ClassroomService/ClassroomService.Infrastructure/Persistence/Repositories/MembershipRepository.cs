using ClassroomService.Application.Abstractions;
using ClassroomService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassroomService.Infrastructure.Persistence.Repositories;

public sealed class MembershipRepository : GenericRepository<ClassroomMembership>, IMembershipRepository
{
    public MembershipRepository(ApplicationDbContext context) : base(context) { }

    public async Task<bool> IsEnrolledAsync(Guid classroomId, Guid studentId, CancellationToken ct)
    {
        return await _context.Set<ClassroomMembership>()
            .AnyAsync(m => m.ClassroomId == classroomId && m.StudentId == studentId, ct);
    }

    public async Task<List<ClassroomMembership>> GetMembersWithDetailsAsync(Guid classroomId, CancellationToken ct)
    {
        return await _context.Set<ClassroomMembership>()
            .Where(m => m.ClassroomId == classroomId)
            .OrderBy(m => m.JoinedAtUtc)
            .ToListAsync(ct);
    }

    public async Task<ClassroomMembership?> GetMembershipAsync(Guid classroomId, Guid studentId, CancellationToken ct)
    {
        return await _context.Set<ClassroomMembership>()
            .FirstOrDefaultAsync(m => m.ClassroomId == classroomId && m.StudentId == studentId, ct);
    }
}