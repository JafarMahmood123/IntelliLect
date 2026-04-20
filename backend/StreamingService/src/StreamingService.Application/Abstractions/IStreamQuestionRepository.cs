using StreamingService.Domain.Entities;

namespace StreamingService.Application.Abstractions;

public interface IStreamQuestionRepository : IRepository<StreamQuestion>
{
    Task<IEnumerable<StreamQuestion>> GetByStreamIdAsync(Guid streamId, CancellationToken ct = default);
    Task<(IEnumerable<StreamQuestion> Items, int TotalCount)> GetByStreamIdPagedAsync(
        Guid streamId,
        int page,
        int pageSize,
        CancellationToken ct = default);
}