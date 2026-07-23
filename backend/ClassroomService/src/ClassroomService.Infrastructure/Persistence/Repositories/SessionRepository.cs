using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Session;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
using ClassroomService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ClassroomService.Infrastructure.Persistence.Repositories;

public class SessionRepository : ISessionRepository
{
    private readonly ApplicationDbContext _context;

    public SessionRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Session?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.Sessions
            .FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<IEnumerable<Session>> GetByClassroomIdAsync(Guid classroomId, CancellationToken ct = default)
    {
        return await _context.Sessions
            // A session being deleted (PendingDeletion) is out of use, so it must not appear on the
            // teacher/student classroom session list. The super-admin admin listing keeps it visible.
            .Where(s => s.ClassroomId == classroomId && s.Status != SessionStatus.PendingDeletion)
            // We use AsNoTracking() for read-only lists to improve performance
            .AsNoTracking()
            .OrderByDescending(s => s.ScheduledAtUtc)
            .ToListAsync(ct);
    }

    public async Task AddAsync(Session session, CancellationToken ct = default)
    {
        await _context.Sessions.AddAsync(session, ct);
    }

    public async Task UpdateAsync(Session session, CancellationToken ct = default)
    {
        // Entity Framework tracks changes, so we just ensure the entry state is modified
        _context.Sessions.Update(session);
        await Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _context.SaveChangesAsync(ct);
    }

    public async Task<bool> TryMarkEndedAsync(Guid sessionId, DateTime endedAtUtc, CancellationToken ct = default)
    {
        // Single UPDATE ... WHERE Status = Live. The database arbitrates the race, so a teacher
        // and the stalled sweeper firing together cannot both run the teardown.
        var rows = await _context.Sessions
            .Where(s => s.Id == sessionId && s.Status == SessionStatus.Live)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(s => s.Status, SessionStatus.Ended)
                    .SetProperty(s => s.EndedAtUtc, endedAtUtc),
                ct);

        if (rows > 0)
        {
            // ExecuteUpdate bypasses the change tracker; drop any stale tracked copy so a later
            // read in the same scope does not resurrect the old status.
            var tracked = _context.ChangeTracker.Entries<Session>()
                .FirstOrDefault(e => e.Entity.Id == sessionId);
            if (tracked is not null)
            {
                tracked.State = EntityState.Detached;
            }
        }

        return rows > 0;
    }

    public async Task<List<Guid>> GetStalledLiveSessionIdsAsync(
        DateTime startedBeforeUtc, int limit, CancellationToken ct = default)
    {
        return await _context.Sessions
            .AsNoTracking()
            .Where(s => s.Status == SessionStatus.Live
                        && (s.StartedAtUtc.HasValue ? s.StartedAtUtc.Value : s.CreatedAtUtc) <= startedBeforeUtc)
            // Oldest first: the most overdue sessions are closed even when a cycle hits the limit.
            .OrderBy(s => s.StartedAtUtc ?? s.CreatedAtUtc)
            .Take(limit)
            .Select(s => s.Id)
            .ToListAsync(ct);
    }

    public async Task<(List<AdminSessionResponse> Items, int TotalCount)> GetAdminSessionsPagedAsync(
        int page, int pageSize, string? search, SessionStatus? status, Guid? classroomId, CancellationToken ct = default)
    {
        var query = _context.Sessions.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(s => EF.Functions.ILike(s.Title, term));
        }
        if (status.HasValue)
        {
            query = query.Where(s => s.Status == status.Value);
        }
        if (classroomId.HasValue && classroomId.Value != Guid.Empty)
        {
            query = query.Where(s => s.ClassroomId == classroomId.Value);
        }

        var totalCount = await query.CountAsync(ct);

        // Project with correlated subqueries for the classroom (name/teacher) and the
        // recording/summary status. Enum statuses are materialized then mapped to strings.
        var rows = await query
            .OrderByDescending(s => s.ScheduledAtUtc)
            .ThenBy(s => s.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new
            {
                s.Id,
                s.ClassroomId,
                ClassName = _context.Set<Classroom>().Where(c => c.Id == s.ClassroomId).Select(c => c.Name).FirstOrDefault(),
                TeacherId = _context.Set<Classroom>().Where(c => c.Id == s.ClassroomId).Select(c => c.TeacherId).FirstOrDefault(),
                s.Title,
                s.Status,
                s.ScheduledAtUtc,
                s.StartedAtUtc,
                s.EndedAtUtc,
                s.CreatedAtUtc,
                RecordingStatus = _context.Set<SessionRecording>().Where(r => r.SessionId == s.Id)
                    .Select(r => (RecordingStatus?)r.Status).FirstOrDefault(),
                SummaryStatus = _context.Set<SessionSummary>().Where(x => x.SessionId == s.Id)
                    .Select(x => (SummaryStatus?)x.Status).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var items = rows.Select(r => new AdminSessionResponse
        {
            SessionId = r.Id,
            ClassroomId = r.ClassroomId,
            ClassName = r.ClassName ?? string.Empty,
            TeacherId = r.TeacherId,
            Title = r.Title,
            Status = r.Status.ToString(),
            ScheduledAtUtc = r.ScheduledAtUtc,
            StartedAtUtc = r.StartedAtUtc,
            EndedAtUtc = r.EndedAtUtc,
            RecordingStatus = r.RecordingStatus?.ToString(),
            SummaryStatus = r.SummaryStatus?.ToString(),
            CreatedAtUtc = r.CreatedAtUtc,
        }).ToList();

        return (items, totalCount);
    }
}