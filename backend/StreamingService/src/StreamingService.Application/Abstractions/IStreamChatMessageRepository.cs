using StreamingService.Domain.Entities;

namespace StreamingService.Application.Abstractions;

public interface IStreamChatMessageRepository : IRepository<StreamChatMessage>
{
    Task<IEnumerable<StreamChatMessage>> GetByStreamIdAsync(Guid streamId, CancellationToken ct = default);
    Task<(IEnumerable<StreamChatMessage> Items, int TotalCount)> GetByStreamIdPagedAsync(
        Guid streamId,
        int page,
        int pageSize,
        CancellationToken ct = default);
}