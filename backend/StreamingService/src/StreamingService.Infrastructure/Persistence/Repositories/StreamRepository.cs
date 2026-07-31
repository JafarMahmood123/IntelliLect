using Microsoft.EntityFrameworkCore;
using StreamingService.Application.Abstractions;
using StreamingService.Domain.Entities;
using StreamingService.Domain.Enums;

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

    public async Task<bool> TryClaimEgressSlotAsync(
        Guid sessionId, string placeholderEgressId, CancellationToken ct = default)
    {
        // Conditional UPDATE: the WHERE is the race arbiter, so exactly one concurrent caller can
        // observe a row affected. ExecuteUpdate bypasses the change tracker and is its own
        // statement — no SaveChanges needed.
        var affected = await _streamingContext.Streams
            .Where(s => s.SessionId == sessionId && s.EgressId == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(s => s.EgressId, placeholderEgressId), ct);

        return affected > 0;
    }

    public async Task SetEgressIdAsync(Guid streamId, string? egressId, CancellationToken ct = default)
    {
        await _streamingContext.Streams
            .Where(s => s.Id == streamId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.EgressId, egressId), ct);
    }

    public async Task<List<LiveStream>> GetLiveStreamsNeedingRecordingAsync(
        string placeholderPrefix, DateTime claimedBeforeUtc, CancellationToken ct = default)
    {
        // Narrow in SQL to live streams the teacher WANTS recorded that are unrecorded or still
        // holding a placeholder. The RecordingState filter is what keeps this from fighting the
        // teacher: without it, a session with recording off (the default) or already stopped would
        // be treated as broken and started on every pass. The StartedAtUtc bound gives the
        // room_started webhook — the normal path — time to arrive first; without it this would race
        // the webhook on every freshly-live session and try to attach egress to a room that does
        // not exist yet.
        var candidates = await _streamingContext.Streams
            .AsNoTracking()
            .Where(s => s.Status == StreamStatus.Live
                        && s.RecordingState == RecordingState.Recording
                        && s.StartedAtUtc != null
                        && s.StartedAtUtc <= claimedBeforeUtc
                        && (s.EgressId == null || s.EgressId.StartsWith(placeholderPrefix)))
            .ToListAsync(ct);

        // ...then apply the staleness rule in memory. The claim timestamp is encoded in the
        // placeholder itself rather than a new column, so this needs no migration; there are only
        // ever a handful of live streams, so the filtering cost is irrelevant.
        return candidates
            .Where(s => s.EgressId is null
                        || IsAbandonedClaim(s.EgressId, placeholderPrefix, claimedBeforeUtc))
            .ToList();
    }

    private static bool IsAbandonedClaim(string egressId, string prefix, DateTime claimedBeforeUtc)
    {
        var raw = egressId[prefix.Length..];
        // An unparseable placeholder can never expire on its own, so treat it as abandoned rather
        // than letting one malformed value block a session's recording forever.
        if (!long.TryParse(raw, out var ticks)) return true;

        return new DateTime(ticks, DateTimeKind.Utc) <= claimedBeforeUtc;
    }
}