using ClassroomService.Application.Abstractions;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
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

    public async Task<SessionRecording?> GetByIdAsync(Guid recordingId, CancellationToken ct = default)
    {
        return await _context.SessionRecordings
            .FirstOrDefaultAsync(r => r.Id == recordingId, ct);
    }

    public async Task<(IEnumerable<SessionRecording> Items, int TotalCount)> ListByClassroomAsync(
        Guid classroomId,
        Guid? sessionId,
        RecordingStatus? status,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        // Filter directly on the denormalized ClassroomId column (indexed in R-1) — no join needed.
        var query = _context.SessionRecordings
            .Where(r => r.ClassroomId == classroomId);

        if (sessionId.HasValue)
        {
            query = query.Where(r => r.SessionId == sessionId.Value);
        }

        if (status.HasValue)
        {
            query = query.Where(r => r.Status == status.Value);
        }

        query = query.OrderByDescending(r => r.CreatedAtUtc);

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task<List<SessionRecording>> GetStuckProcessingAsync(DateTime olderThanUtc, CancellationToken ct = default)
    {
        return await _context.SessionRecordings
            .Where(r => r.Status == RecordingStatus.Processing && r.CreatedAtUtc < olderThanUtc)
            .ToListAsync(ct);
    }

    public async Task<List<SessionRecording>> GetOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        return await _context.SessionRecordings
            .Where(r => r.CreatedAtUtc < cutoffUtc)
            .ToListAsync(ct);
    }

    public Task RemoveAsync(SessionRecording recording, CancellationToken ct = default)
    {
        _context.SessionRecordings.Remove(recording);
        return Task.CompletedTask;
    }

    public async Task<int> CountProcessingAsync(CancellationToken ct = default)
    {
        return await _context.SessionRecordings
            .CountAsync(r => r.Status == RecordingStatus.Processing, ct);
    }
}
