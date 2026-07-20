using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Classroom;
using ClassroomService.Application.Exceptions;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ClassroomService.Infrastructure.Persistence.Repositories;

public sealed class ClassroomRepository : GenericRepository<Classroom>, IClassroomRepository
{
    public ClassroomRepository(ApplicationDbContext context) : base(context) { }

    // A classroom being deleted (PendingDeletion) is out of use, so it must not appear on any
    // teacher/student read path — these three all filter to Active only. The super-admin admin
    // listing deliberately keeps PendingDeletion rows visible (see GetAdminPagedAsync) so a stuck
    // deletion can be seen and re-run (6أ).
    public async Task<Classroom?> GetWithDetailsAsync(Guid id, CancellationToken ct)
    {
        return await _context.Set<Classroom>()
            .Include(c => c.Files)
            .Include(c => c.Memberships)
            .FirstOrDefaultAsync(c => c.Id == id && c.Status == ClassroomStatus.Active, ct);
    }

    public async Task<List<Classroom>> GetByTeacherIdAsync(Guid teacherId, CancellationToken ct)
    {
        return await _context.Set<Classroom>()
            .Where(c => c.TeacherId == teacherId && c.Status == ClassroomStatus.Active)
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
            .Where(c => classroomIds.Contains(c.Id) && c.Status == ClassroomStatus.Active)
            .Include(c => c.Files)
            .Include(c => c.Memberships)
            .ToListAsync(ct);
    }

    public async Task<(List<AdminClassroomResponse> Items, int TotalCount)> GetAdminPagedAsync(
        int page, int pageSize, string? search, Guid? teacherId, CancellationToken ct = default)
    {
        var query = _context.Set<Classroom>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(c => EF.Functions.ILike(c.Name, term) || EF.Functions.ILike(c.Description, term));
        }

        if (teacherId.HasValue && teacherId.Value != Guid.Empty)
        {
            query = query.Where(c => c.TeacherId == teacherId.Value);
        }

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(c => c.CreatedAtUtc)
            .ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new AdminClassroomResponse
            {
                Id = c.Id,
                Name = c.Name,
                Description = c.Description,
                TeacherId = c.TeacherId,
                CreatedAtUtc = c.CreatedAtUtc,
                FileCount = c.Files.Count,
                StudentCount = c.Memberships.Count,
                SessionCount = _context.Set<Session>().Count(s => s.ClassroomId == c.Id),
                Version = (long)EF.Property<uint>(c, "xmin"),
                Status = c.Status == ClassroomStatus.PendingDeletion ? "PendingDeletion" : "Active",
            })
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task<AdminClassroomResponse?> GetAdminByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.Set<Classroom>()
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new AdminClassroomResponse
            {
                Id = c.Id,
                Name = c.Name,
                Description = c.Description,
                TeacherId = c.TeacherId,
                CreatedAtUtc = c.CreatedAtUtc,
                FileCount = c.Files.Count,
                StudentCount = c.Memberships.Count,
                SessionCount = _context.Set<Session>().Count(s => s.ClassroomId == c.Id),
                Version = (long)EF.Property<uint>(c, "xmin"),
                Status = c.Status == ClassroomStatus.PendingDeletion ? "PendingDeletion" : "Active",
            })
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> UpdateWithConcurrencyAsync(
        Guid id, string name, string description, long expectedVersion, CancellationToken ct = default)
    {
        var classroom = await _context.Set<Classroom>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (classroom == null)
        {
            return false;
        }

        classroom.Name = name;
        classroom.Description = description;

        // Force the UPDATE's WHERE clause to use the version the caller last read, so a row
        // changed since then yields zero affected rows -> DbUpdateConcurrencyException (6أ),
        // surfaced as a ConflictException (maps to HTTP 409).
        _context.Entry(classroom).Property("xmin").OriginalValue = (uint)expectedVersion;

        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The classroom was modified by someone else. Reload and try again.");
        }

        return true;
    }
}