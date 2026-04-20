using Microsoft.EntityFrameworkCore;
using StreamingService.Application.Abstractions;
using StreamingService.Domain.Entities;

namespace StreamingService.Infrastructure.Persistence.Repositories;

public sealed class StreamQuestionRepository : GenericRepository<StreamQuestion>, IStreamQuestionRepository
{
    public StreamQuestionRepository(StreamingDbContext context) : base(context) { }

    public async Task<IEnumerable<StreamQuestion>> GetByStreamIdAsync(Guid streamId, CancellationToken ct = default)
    {
        return await _context.Set<StreamQuestion>()
            .Where(q => q.StreamId == streamId)
            .OrderByDescending(q => q.AskedAtUtc)
            .ToListAsync(ct);
    }

    public async Task<(IEnumerable<StreamQuestion> Items, int TotalCount)> GetByStreamIdPagedAsync(
        Guid streamId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var query = _context.Set<StreamQuestion>()
            .Where(q => q.StreamId == streamId)
            .OrderByDescending(q => q.AskedAtUtc);

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }
}