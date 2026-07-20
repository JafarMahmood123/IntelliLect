using Microsoft.EntityFrameworkCore;
using StreamingService.Application.Abstractions;
using StreamingService.Domain.Entities;

namespace StreamingService.Infrastructure.Persistence.Repositories;

public sealed class StreamRepository : GenericRepository<LiveStream>, IStreamRepository
{
    private readonly StreamingDbContext _streamingContext;

    public StreamRepository(StreamingDbContext context) : base(context)
    {
        _streamingContext = context;
    }

    public async Task<LiveStream?> GetBySessionIdAsync(Guid sessionId, bool includeParticipants, CancellationToken ct)
    {
        var query = _streamingContext.Streams.AsQueryable();

        if (includeParticipants)
            query = query.Include(s => s.Participants);

        return await query.FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);
    }

    public async Task<bool> ExistsAsync(Guid sessionId, CancellationToken ct)
    {
        return await _streamingContext.Streams.AnyAsync(s => s.SessionId == sessionId, ct);
    }

    public async Task<LiveStream?> GetByEgressIdAsync(string egressId, CancellationToken ct)
    {
        return await _streamingContext.Streams.FirstOrDefaultAsync(s => s.EgressId == egressId, ct);
    }

    public async Task<List<LiveStream>> GetLiveStreamsAsync(CancellationToken ct = default)
    {
        return await _streamingContext.Streams
            .AsNoTracking()
            .Include(s => s.Participants)
            .Where(s => s.Status == StreamStatus.Live)
            .OrderByDescending(s => s.StartedAtUtc)
            .ToListAsync(ct);
    }
}