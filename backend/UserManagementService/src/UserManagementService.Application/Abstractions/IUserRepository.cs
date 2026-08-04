using UserManagementService.Application.Common;
using UserManagementService.Application.Common.Users;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Application.Abstractions;

public interface IUserRepository : IRepository<User>
{
    Task<User?> FindByEmail(string email, CancellationToken cancellationToken = default);
    Task<User?> GetByIdWithRefreshTokensAsync(Guid id, CancellationToken ct = default);
    Task<List<User>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// Loads accounts together with their refresh tokens, for a bulk status change.
    ///
    /// Separate from <see cref="GetByIdsAsync"/> because rejection and deactivation must revoke
    /// active sessions in the same transaction, and that needs the tokens loaded. One query for
    /// the whole batch rather than one per account.
    /// </summary>
    Task<List<User>> GetByIdsWithRefreshTokensAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    Task<User?> FindByRefreshToken(string token, CancellationToken ct);
    Task<User?> FindByResetToken(string token, CancellationToken ct);
    Task<(List<User> Items, int TotalCount)> GetPaginatedPendingUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct);
    Task<(List<User> Items, int TotalCount)> GetPaginatedUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct);
    Task<(List<User> Items, int TotalCount)> SearchUsersAsync(UserQuerySpecification specification, CancellationToken ct = default);
}