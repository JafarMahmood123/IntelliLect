using Microsoft.EntityFrameworkCore;
using StreamingService.Application.Abstractions;
using StreamingService.Domain.Entities;

namespace StreamingService.Infrastructure.Persistence.Repositories;

public sealed class StreamChatMessageRepository : GenericRepository<StreamChatMessage>, IStreamChatMessageRepository
{
    public StreamChatMessageRepository(StreamingDbContext context) : base(context) { }

    public async Task<IEnumerable<StreamChatMessage>> GetByStreamIdAsync(Guid streamId, CancellationToken ct = default)
    {
        return await _context.Set<StreamChatMessage>()
            .Where(m => m.StreamId == streamId)
            .OrderBy(m => m.SentAtUtc)
            .ToListAsync(ct);
    }

    public async Task<(IEnumerable<StreamChatMessage> Items, int TotalCount)> GetByStreamIdPagedAsync(
        Guid streamId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var query = _context.Set<StreamChatMessage>()
            .Where(m => m.StreamId == streamId)
            .OrderBy(m => m.SentAtUtc);

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }
}