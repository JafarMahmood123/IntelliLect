using StreamingService.Domain.Entities;

namespace StreamingService.Application.Abstractions;

public interface IStreamRepository : IRepository<LiveStream>
{
    Task<LiveStream?> GetBySessionIdAsync(Guid sessionId, bool includeParticipants = false, CancellationToken ct = default);
    Task<bool> ExistsAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Correlates a LiveKit egress back to its stream (R-1 webhook path).</summary>
    Task<LiveStream?> GetByEgressIdAsync(string egressId, CancellationToken ct = default);
}