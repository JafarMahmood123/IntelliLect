using StreamingService.Domain.Entities;

namespace StreamingService.Application.Abstractions;

public interface IParticipantRepository : IRepository<StreamParticipant>
{
    Task<StreamParticipant?> GetBySessionAndUserAsync(Guid sessionId, Guid userId, CancellationToken ct = default);
    Task<bool> IsUserInStreamAsync(Guid streamId, Guid userId, CancellationToken ct = default);
}