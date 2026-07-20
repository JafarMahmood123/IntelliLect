using UserManagementService.Application.Common;
using UserManagementService.Application.Common.Users;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Application.Abstractions;

public interface IUserRepository : IRepository<User>
{
    Task<User?> FindByEmail(string email, CancellationToken cancellationToken = default);
    Task<User?> GetByIdWithRefreshTokensAsync(Guid id, CancellationToken ct = default);
    Task<List<User>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    Task<User?> FindByRefreshToken(string token, CancellationToken ct);
    Task<User?> FindByResetToken(string token, CancellationToken ct);
    Task<(List<User> Items, int TotalCount)> GetPaginatedPendingUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct);
    Task<(List<User> Items, int TotalCount)> GetPaginatedUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct);
    Task<(List<User> Items, int TotalCount)> SearchUsersAsync(UserQuerySpecification specification, CancellationToken ct = default);
}