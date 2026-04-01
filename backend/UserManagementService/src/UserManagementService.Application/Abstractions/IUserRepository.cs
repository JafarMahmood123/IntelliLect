using UserManagementService.Domain.Entities;

namespace UserManagementService.Application.Abstractions
{
    public interface IUserRepository : IRepository<User>
    {
        Task<User?> FindByEmail(string email, CancellationToken cancellationToken = default);
        Task<User?> FindByRefreshToken(string token, CancellationToken ct);
        Task<User?> FindByResetToken(string token, CancellationToken ct);
    }
}