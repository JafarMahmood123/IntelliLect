using Microsoft.EntityFrameworkCore;
using UserManagementService.Application.Abstractions;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Infrastructure.Persistence.Repositories;

public sealed class TwoFactorChallengeRepository : GenericRepository<TwoFactorChallenge>, ITwoFactorChallengeRepository
{
    public TwoFactorChallengeRepository(ApplicationDbContext context) : base(context)
    {
    }

    public async Task<TwoFactorChallenge?> FindByUserId(Guid userId, CancellationToken ct = default)
    {
        return await _context.TwoFactorChallenges
            .FirstOrDefaultAsync(c => c.UserId == userId, ct);
    }
}
