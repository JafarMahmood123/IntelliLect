using UserManagementService.Domain.Entities;

namespace UserManagementService.Application.Abstractions;

public interface IResetTokenRepository : IRepository<ResetPasswordToken>
{
    Task<ResetPasswordToken?> FindResetPasswordTokenByUserId(Guid userId);
}
