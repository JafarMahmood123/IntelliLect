using AutoMapper;
using IntelliLect.Contracts.Messages;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;
using UserManagementService.Application.Common.Users;
using UserManagementService.Application.UserAccounts;
using UserManagementService.Domain.Entities;

namespace UserManagementService.UnitTests.UserAccounts;

// Unit tests for UserStatusService, mirroring the "إدارة حالة حساب مستخدم" use-case:
//   Main path      -> the four valid transitions (Accept/Reject/Deactivate/Reactivate),
//                     with notification (step 7) and session termination on deactivate/reject (step 6).
//   Alternate 5أ   -> target account not found.
//   Alternate 5ب   -> target is the acting super admin's own account.
//   Alternate 5ج   -> invalid transition for the current state.
//   Alternate 5د   -> already in the requested state (idempotent no-op).
public class UserStatusServiceTests
{
    private static readonly Guid SuperAdminId = Guid.NewGuid();

    // ----- main path: valid transitions ---------------------------------------

    [Fact]
    public async Task ChangeStatus_AcceptPendingUser_ActivatesAndNotifies_WithoutRevokingSessions()
    {
        var user = UserWith(UserStatus.Pending);
        var (repo, bus) = Fakes(user);
        var sut = CreateSut(repo, bus);

        var result = await sut.ChangeStatusAsync(user.Id, "Accept", SuperAdminId);

        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Equal(UserStatus.Active.ToString(), result.Status);
        Assert.Equal(1, repo.SaveChangesCallCount);
        AssertNotified(bus, UserStatus.Active);
        // Approving a registration does not end sessions.
    }

    [Fact]
    public async Task ChangeStatus_RejectPendingUser_RejectsRevokesSessionsAndNotifies()
    {
        var user = UserWith(UserStatus.Pending, ActiveToken(), ActiveToken());
        var (repo, bus) = Fakes(user);
        var sut = CreateSut(repo, bus);

        await sut.ChangeStatusAsync(user.Id, "Reject", SuperAdminId);

        Assert.Equal(UserStatus.Rejected, user.Status);
        Assert.All(user.RefreshTokens, t => Assert.True(t.IsRevoked));
        Assert.Equal(1, repo.SaveChangesCallCount);
        AssertNotified(bus, UserStatus.Rejected);
    }

    [Fact]
    public async Task ChangeStatus_DeactivateActiveUser_DeactivatesRevokesActiveSessionsAndNotifies()
    {
        var alreadyRevoked = ActiveToken();
        alreadyRevoked.Revoke();
        var user = UserWith(UserStatus.Active, ActiveToken(), alreadyRevoked);
        var (repo, bus) = Fakes(user);
        var sut = CreateSut(repo, bus);

        await sut.ChangeStatusAsync(user.Id, "Deactivate", SuperAdminId);

        Assert.Equal(UserStatus.Deactivated, user.Status);
        // Step 6: every session is now revoked (both the previously active and the already-revoked one).
        Assert.All(user.RefreshTokens, t => Assert.True(t.IsRevoked));
        Assert.Equal(1, repo.SaveChangesCallCount);
        AssertNotified(bus, UserStatus.Deactivated);
    }

    [Fact]
    public async Task ChangeStatus_ReactivateDeactivatedUser_ActivatesAndNotifies()
    {
        var user = UserWith(UserStatus.Deactivated);
        var (repo, bus) = Fakes(user);
        var sut = CreateSut(repo, bus);

        var result = await sut.ChangeStatusAsync(user.Id, "Reactivate", SuperAdminId);

        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Equal(UserStatus.Active.ToString(), result.Status);
        Assert.Equal(1, repo.SaveChangesCallCount);
        AssertNotified(bus, UserStatus.Active);
    }

    [Fact]
    public async Task ChangeStatus_ActionIsCaseInsensitive()
    {
        var user = UserWith(UserStatus.Pending);
        var (repo, bus) = Fakes(user);
        var sut = CreateSut(repo, bus);

        await sut.ChangeStatusAsync(user.Id, "accept", SuperAdminId);

        Assert.Equal(UserStatus.Active, user.Status);
    }

    // ----- alternate paths -----------------------------------------------------

    [Fact]
    public async Task ChangeStatus_WhenUserNotFound_ThrowsNotFound()
    {
        // Alternate path 5أ.
        var (repo, bus) = Fakes(user: null);
        var sut = CreateSut(repo, bus);

        await Assert.ThrowsAsync<NotFoundException>(
            () => sut.ChangeStatusAsync(Guid.NewGuid(), "Deactivate", SuperAdminId));

        Assert.Equal(0, repo.SaveChangesCallCount);
        Assert.Empty(bus.Published);
    }

    [Fact]
    public async Task ChangeStatus_WhenTargetIsSelf_ThrowsAndDoesNotTouchRepository()
    {
        // Alternate path 5ب: the self-check runs before any lookup or write.
        var (repo, bus) = Fakes(user: null);
        var sut = CreateSut(repo, bus);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ChangeStatusAsync(SuperAdminId, "Deactivate", SuperAdminId));

        Assert.False(repo.GetByIdWithRefreshTokensCalled);
        Assert.Equal(0, repo.SaveChangesCallCount);
        Assert.Empty(bus.Published);
    }

    [Theory]
    [InlineData(UserStatus.Rejected, "Accept")]     // cannot accept a rejected account
    [InlineData(UserStatus.Pending, "Deactivate")]  // cannot deactivate an account not yet active
    [InlineData(UserStatus.Pending, "Reactivate")]  // cannot reactivate an account that was never deactivated
    [InlineData(UserStatus.Active, "Reject")]       // cannot reject an already-active account
    [InlineData(UserStatus.Deactivated, "Accept")]  // cannot accept a deactivated account (use reactivate)
    public async Task ChangeStatus_WithInvalidTransition_ThrowsAndDoesNotChange(UserStatus current, string action)
    {
        // Alternate path 5ج.
        var user = UserWith(current, ActiveToken());
        var (repo, bus) = Fakes(user);
        var sut = CreateSut(repo, bus);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ChangeStatusAsync(user.Id, action, SuperAdminId));

        Assert.Equal(current, user.Status);
        Assert.False(user.RefreshTokens.Single().IsRevoked);
        Assert.Equal(0, repo.SaveChangesCallCount);
        Assert.Empty(bus.Published);
    }

    [Theory]
    [InlineData(UserStatus.Active, "Accept")]         // already active
    [InlineData(UserStatus.Active, "Reactivate")]     // already active
    [InlineData(UserStatus.Deactivated, "Deactivate")]// already deactivated
    [InlineData(UserStatus.Rejected, "Reject")]       // already rejected
    public async Task ChangeStatus_WhenAlreadyInRequestedState_IsNoOp(UserStatus current, string action)
    {
        // Alternate path 5د: no change, no notification, no save.
        var user = UserWith(current, ActiveToken());
        var (repo, bus) = Fakes(user);
        var sut = CreateSut(repo, bus);

        var result = await sut.ChangeStatusAsync(user.Id, action, SuperAdminId);

        Assert.Equal(current, user.Status);
        Assert.Equal(current.ToString(), result.Status);
        Assert.False(user.RefreshTokens.Single().IsRevoked);
        Assert.Equal(0, repo.SaveChangesCallCount);
        Assert.Empty(bus.Published);
    }

    [Fact]
    public async Task ChangeStatus_WithUnknownAction_ThrowsArgument()
    {
        var (repo, bus) = Fakes(user: null);
        var sut = CreateSut(repo, bus);

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.ChangeStatusAsync(Guid.NewGuid(), "Explode", SuperAdminId));
    }

    // ----- helpers -------------------------------------------------------------

    private static UserStatusService CreateSut(FakeStatusUserRepository repo, RecordingStatusEventBus bus)
        => new(repo, bus, BuildMapper());

    private static (FakeStatusUserRepository repo, RecordingStatusEventBus bus) Fakes(User? user)
        => (new FakeStatusUserRepository(user), new RecordingStatusEventBus());

    private static IMapper BuildMapper()
        => new MapperConfiguration(cfg => cfg.AddProfile<UserProfile>()).CreateMapper();

    private static void AssertNotified(RecordingStatusEventBus bus, UserStatus expected)
    {
        var message = Assert.Single(bus.Published.OfType<UserStatusChangedMessage>());
        Assert.Equal(expected.ToString(), message.Status);
    }

    private static RefreshToken ActiveToken() => new()
    {
        Id = Guid.NewGuid(),
        Token = Guid.NewGuid().ToString("N"),
        IsRevoked = false,
        ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
    };

    private static User UserWith(UserStatus status, params RefreshToken[] tokens)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = "user",
            Email = "user@intellilect.io",
            FirstName = "First",
            LastName = "Last",
            PasswordHash = "H:pass",
            RoleId = Guid.NewGuid(),
            Role = Role.Create(RoleName.Student),
            RefreshTokens = tokens.ToList(),
        };
        // Drive the entity into the desired starting state via its own transitions.
        switch (status)
        {
            case UserStatus.Active: user.Approve(); break;
            case UserStatus.Rejected: user.Reject(); break;
            case UserStatus.Deactivated: user.Deactivate(); break;
            case UserStatus.Pending: break; // default
        }
        return user;
    }
}

// ----- fakes ------------------------------------------------------------------

internal sealed class FakeStatusUserRepository : IUserRepository
{
    private readonly User? _user;
    public FakeStatusUserRepository(User? user) => _user = user;

    public bool GetByIdWithRefreshTokensCalled { get; private set; }
    public int SaveChangesCallCount { get; private set; }

    public Task<User?> GetByIdWithRefreshTokensAsync(Guid id, CancellationToken ct = default)
    {
        GetByIdWithRefreshTokensCalled = true;
        return Task.FromResult(_user);
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveChangesCallCount++;
        return Task.FromResult(1);
    }

    public Task<User?> FindByEmail(string email, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<List<User>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<User?> FindByRefreshToken(string token, CancellationToken ct) => throw new NotImplementedException();
    public Task<User?> FindByResetToken(string token, CancellationToken ct) => throw new NotImplementedException();
    public Task<(List<User> Items, int TotalCount)> GetPaginatedPendingUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct) => throw new NotImplementedException();
    public Task<(List<User> Items, int TotalCount)> GetPaginatedUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct) => throw new NotImplementedException();
    public Task<(List<User> Items, int TotalCount)> SearchUsersAsync(UserQuerySpecification specification, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task AddAsync(User entity, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task UpdateAsync(User entity, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
}

internal sealed class RecordingStatusEventBus : IEventBus
{
    public List<object> Published { get; } = new();

    public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class
    {
        Published.Add(message);
        return Task.CompletedTask;
    }
}
