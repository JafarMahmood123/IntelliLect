using UserManagementService.Domain.Entities;

namespace UserManagementService.Application.Abstractions
{
    public interface IUserRepository : IRepository<User>
    {
        Task<User?> FindByEmail(string email, CancellationToken cancellationToken = default);
    }
}