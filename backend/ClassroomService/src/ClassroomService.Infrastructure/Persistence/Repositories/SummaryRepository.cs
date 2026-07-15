using ClassroomService.Application.Abstractions;
using ClassroomService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassroomService.Infrastructure.Persistence.Repositories;

public sealed class SummaryRepository : ISummaryRepository
{
    private readonly ApplicationDbContext _context;

    public SummaryRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(SessionSummary summary, CancellationToken ct = default)
    {
        await _context.SessionSummaries.AddAsync(summary, ct);
    }

    public async Task<SessionSummary?> GetBySessionIdAsync(Guid sessionId, CancellationToken ct = default)
    {
        return await _context.SessionSummaries
            .FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);
    }

    public async Task<SessionSummary?> GetByIdAsync(Guid summaryId, CancellationToken ct = default)
    {
        return await _context.SessionSummaries
            .FirstOrDefaultAsync(s => s.Id == summaryId, ct);
    }

    public async Task<(IEnumerable<SessionSummary> Items, int TotalCount)> ListByClassroomAsync(
        Guid classroomId,
        Guid? sessionId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        // Filter directly on the denormalized (indexed) ClassroomId column — no join needed.
        var query = _context.SessionSummaries
            .Where(s => s.ClassroomId == classroomId);

        if (sessionId.HasValue)
        {
            query = query.Where(s => s.SessionId == sessionId.Value);
        }

        query = query.OrderByDescending(s => s.CreatedAtUtc);

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }
}
