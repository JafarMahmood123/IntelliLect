using ClassroomService.Application.Abstractions;
using ClassroomService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassroomService.Infrastructure.Persistence.Repositories;

public sealed class RecordingRepository : IRecordingRepository
{
    private readonly ApplicationDbContext _context;

    public RecordingRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(SessionRecording recording, CancellationToken ct = default)
    {
        await _context.SessionRecordings.AddAsync(recording, ct);
    }

    public async Task<SessionRecording?> GetBySessionIdAsync(Guid sessionId, CancellationToken ct = default)
    {
        return await _context.SessionRecordings
            .FirstOrDefaultAsync(r => r.SessionId == sessionId, ct);
    }

    public async Task<(IEnumerable<SessionRecording> Items, int TotalCount)> GetByClassroomIdPagedAsync(
        Guid classroomId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var query = _context.SessionRecordings
            .Include(r => r.Session)
            .Where(r => r.Session.ClassroomId == classroomId)
            .OrderByDescending(r => r.CreatedAtUtc);

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }
}