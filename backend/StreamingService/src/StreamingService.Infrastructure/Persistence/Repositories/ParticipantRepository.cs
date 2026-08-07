using Microsoft.EntityFrameworkCore;
using StreamingService.Application.Abstractions;
using StreamingService.Domain.Entities;

namespace StreamingService.Infrastructure.Persistence.Repositories;

public sealed class ParticipantRepository : GenericRepository<StreamParticipant>, IParticipantRepository
{
    private readonly StreamingDbContext _streamingContext;

    public ParticipantRepository(StreamingDbContext context) : base(context)
    {
        _streamingContext = context;
    }

    public async Task<StreamParticipant?> GetBySessionAndUserAsync(Guid sessionId, Guid userId, CancellationToken ct)
    {
        return await _streamingContext.Participants
            .FirstOrDefaultAsync(p => p.Stream.SessionId == sessionId && p.UserId == userId, ct);
    }

    public async Task<bool> IsUserInStreamAsync(Guid streamId, Guid userId, CancellationToken ct)
    {
        return await _streamingContext.Participants
            .AnyAsync(p => p.StreamId == streamId && p.UserId == userId, ct);
    }

    public async Task<int> CountInStreamAsync(Guid streamId, CancellationToken ct)
    {
        return await _streamingContext.Participants.CountAsync(p => p.StreamId == streamId, ct);
    }
}