using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Authentication;
using UserManagementService.Domain.Entities;

namespace UserManagementService.UnitTests.Authentication;

// Unit tests for AuthService.LogoutAsync, mirroring the "تسجيل الخروج" use-case in the report:
//   Main path        -> a user revokes their own active refresh token.
//   Alternate path 3 -> the token is missing or already revoked: logout still succeeds (idempotent).
//   Alternate path 4 -> the token belongs to another user: the request is refused, nothing is revoked.
public class AuthServiceLogoutTests
{
    private const string TokenValue = "refresh-token-abc";

    [Fact]
    public async Task LogoutAsync_WithOwnActiveToken_RevokesTokenAndSaves()
    {
        // Arrange: an active token owned by the caller.
        var userId = Guid.NewGuid();
        var token = ActiveToken(userId);
        var users = new FakeUserRepository(UserWith(userId, token));
        var refreshTokens = new FakeRefreshTokenRepository();
        var sut = CreateSut(users, refreshTokens);

        // Act
        await sut.LogoutAsync(userId, TokenValue);

        // Assert: token revoked and the change persisted exactly once.
        Assert.True(token.IsRevoked);
        Assert.Equal(1, refreshTokens.SaveChangesCallCount);
    }

    [Fact]
    public async Task LogoutAsync_WhenTokenNotFound_SucceedsWithoutSaving()
    {
        // Arrange: repository returns no user for the token (Alternate path 3).
        var users = new FakeUserRepository(null);
        var refreshTokens = new FakeRefreshTokenRepository();
        var sut = CreateSut(users, refreshTokens);

        // Act + Assert: completes silently; nothing to revoke, so no save.
        await sut.LogoutAsync(Guid.NewGuid(), TokenValue);

        Assert.Equal(0, refreshTokens.SaveChangesCallCount);
    }

    [Fact]
    public async Task LogoutAsync_WhenTokenAlreadyRevoked_SucceedsWithoutSaving()
    {
        // Arrange: the caller's own token, but it was already revoked (Alternate path 3).
        var userId = Guid.NewGuid();
        var token = ActiveToken(userId);
        token.Revoke();
        var users = new FakeUserRepository(UserWith(userId, token));
        var refreshTokens = new FakeRefreshTokenRepository();
        var sut = CreateSut(users, refreshTokens);

        // Act + Assert: goal already met, so no exception and no extra save.
        await sut.LogoutAsync(userId, TokenValue);

        Assert.True(token.IsRevoked);
        Assert.Equal(0, refreshTokens.SaveChangesCallCount);
    }

    [Fact]
    public async Task LogoutAsync_WhenTokenBelongsToAnotherUser_ThrowsAndDoesNotRevoke()
    {
        // Arrange: the token belongs to the owner, but a different user requests logout (Alternate path 4).
        var ownerId = Guid.NewGuid();
        var attackerId = Guid.NewGuid();
        var token = ActiveToken(ownerId);
        var users = new FakeUserRepository(UserWith(ownerId, token));
        var refreshTokens = new FakeRefreshTokenRepository();
        var sut = CreateSut(users, refreshTokens);

        // Act + Assert: request refused; the owner's token stays valid and nothing is saved.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sut.LogoutAsync(attackerId, TokenValue));

        Assert.False(token.IsRevoked);
        Assert.Equal(0, refreshTokens.SaveChangesCallCount);
    }

    // --- helpers ---------------------------------------------------------

    private static RefreshToken ActiveToken(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        Token = TokenValue,
        UserId = userId,
        IsRevoked = false,
        ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
    };

    private static User UserWith(Guid userId, RefreshToken token) => new()
    {
        Id = userId,
        RefreshTokens = new List<RefreshToken> { token },
    };

    private static AuthService CreateSut(FakeUserRepository users, FakeRefreshTokenRepository refreshTokens)
        => new(
            users,
            new NotUsedRepository<Role>(),
            new NotUsedRoleRepository(),
            new NotUsedHasher(),
            new NotUsedJwtProvider(),
            refreshTokens,
            resetPasswordRepository: null!,
            resetPasswordTokenGenerator: null!,
            twoFactorRepository: null!,
            twoFactorCodeGenerator: null!,
            mapper: null!,
            eventBus: null!);
}

// Fakes: only the members LogoutAsync actually exercises are implemented; the rest throw
// so any accidental reliance on them surfaces immediately.

internal sealed class FakeUserRepository : IUserRepository
{
    private readonly User? _user;
    public FakeUserRepository(User? user) => _user = user;

    public Task<User?> FindByRefreshToken(string token, CancellationToken ct) => Task.FromResult(_user);

    public Task<User?> FindByEmail(string email, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<User?> GetByIdWithRefreshTokensAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<User?> FindByResetToken(string token, CancellationToken ct) => throw new NotImplementedException();
    public Task<(List<User> Items, int TotalCount)> GetPaginatedPendingUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct) => throw new NotImplementedException();
    public Task<(List<User> Items, int TotalCount)> GetPaginatedUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct) => throw new NotImplementedException();
    public Task<(List<User> Items, int TotalCount)> SearchUsersAsync(UserManagementService.Application.Common.Users.UserQuerySpecification specification, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task AddAsync(User entity, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task UpdateAsync(User entity, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
}

internal sealed class FakeRefreshTokenRepository : IRepository<RefreshToken>
{
    public int SaveChangesCallCount { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveChangesCallCount++;
        return Task.FromResult(1);
    }

    public Task<RefreshToken?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task AddAsync(RefreshToken entity, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task UpdateAsync(RefreshToken entity, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
}

internal sealed class NotUsedRepository<T> : IRepository<T> where T : class
{
    public Task<T?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task AddAsync(T entity, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task UpdateAsync(T entity, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
}

internal sealed class NotUsedRoleRepository : IRoleRepository
{
    public Task<List<Role>> GetSelfRegistrationRolesAsync(CancellationToken ct = default) => throw new NotImplementedException();
}

internal sealed class NotUsedHasher : IHasher
{
    public string Hash(string code) => throw new NotImplementedException();
    public bool Verify(string oldCode, string codeHash) => throw new NotImplementedException();
}

internal sealed class NotUsedJwtProvider : IJwtProvider
{
    public string GenerateAccessToken(Guid userId, string roleName, string userName, bool twoFactorCompleted = false) => throw new NotImplementedException();
    public string GenerateRefreshToken() => throw new NotImplementedException();
}
