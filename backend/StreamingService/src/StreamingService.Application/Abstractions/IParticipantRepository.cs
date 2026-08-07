using StreamingService.Domain.Entities;

namespace StreamingService.Application.Abstractions;

public interface IParticipantRepository : IRepository<StreamParticipant>
{
    Task<StreamParticipant?> GetBySessionAndUserAsync(Guid sessionId, Guid userId, CancellationToken ct = default);
    Task<bool> IsUserInStreamAsync(Guid streamId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// How many people are in the stream RIGHT NOW, counted in the database.
    ///
    /// Both broadcasts used to do arithmetic on a collection loaded before the write —
    /// <c>Participants.Count + 1</c> on join, <c>- 1</c> on leave. Two people joining at once
    /// both read the same starting number and both announce it plus one, so the count the class
    /// sees is short by one and nothing recomputes it until somebody else joins or leaves.
    /// </summary>
    Task<int> CountInStreamAsync(Guid streamId, CancellationToken ct = default);
}