using Microsoft.EntityFrameworkCore;
using UserManagementService.Application.Abstractions;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Infrastructure.Persistence.Repositories;

public sealed class ResetTokenRepository : GenericRepository<ResetPasswordToken>, IResetTokenRepository
{
    public ResetTokenRepository(ApplicationDbContext context) : base(context)
    {
    }

    public async Task<ResetPasswordToken?> FindResetPasswordTokenByUserId(Guid userId)
    {
        return await _context.ResetPasswordTokens
            .FirstOrDefaultAsync(rt => rt.UserId == userId);
    }
}