using StreamingService.Domain.Entities;

namespace StreamingService.Application.Abstractions;

public interface IStreamRepository : IRepository<LiveStream>
{
    Task<LiveStream?> GetBySessionIdAsync(Guid sessionId, bool includeParticipants = false, CancellationToken ct = default);
    Task<bool> ExistsAsync(Guid sessionId, CancellationToken ct = default);
}