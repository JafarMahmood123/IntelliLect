using UserManagementService.Domain.Entities;

namespace UserManagementService.Application.Abstractions;

public interface ITwoFactorChallengeRepository : IRepository<TwoFactorChallenge>
{
    Task<TwoFactorChallenge?> FindByUserId(Guid userId, CancellationToken ct = default);
}
