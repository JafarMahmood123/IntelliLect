using IntelliLect.Contracts.Messages;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;
using UserManagementService.Application.Common.Admins;
using UserManagementService.Application.DTOs.Admin;
using UserManagementService.Application.SuperAdministration;
using UserManagementService.Domain.Entities;

namespace UserManagementService.UnitTests.SuperAdministration;

// Unit tests for SuperAdminService, mirroring the "إدارة حسابات مدراء النظام" use-case:
//   CreateAdminAsync      -> main path + 5أ (duplicate email) + 5ب (invalid data).
//   Deactivate/Reactivate -> main path + 5ج (target not found) + 5د (target is self).
public class SuperAdminServiceTests
{
    private static readonly Guid SuperAdminId = Guid.NewGuid();

    // ----- CreateAdminAsync ----------------------------------------------------

    [Fact]
    public async Task CreateAdminAsync_WithValidRequest_CreatesActiveAdminAndReturnsId()
    {
        // Arrange: email is free and the Admin role exists.
        var adminRole = Role.Create(RoleName.Admin);
        var users = new FakeUserRepositoryForAdmins(existingByEmail: null);
        var admins = new FakeAdminRepository(adminById: null, adminRole: adminRole);
        var sut = CreateSut(admins, users);

        var request = ValidCreateRequest();

        // Act
        var id = await sut.CreateAdminAsync(request);

        // Assert: an Active admin was persisted with the Admin role and a hashed password.
        Assert.NotNull(users.Added);
        Assert.Equal(id, users.Added!.Id);
        Assert.Equal(adminRole.Id, users.Added.RoleId);
        Assert.Equal(RoleName.Admin, users.Added.Role.Name);
        Assert.Equal(UserStatus.Active, users.Added.Status);
        Assert.Equal($"H:{request.Password}", users.Added.PasswordHash);
        Assert.Equal(1, users.SaveChangesCallCount);
    }

    [Fact]
    public async Task CreateAdminAsync_WithDuplicateEmail_ThrowsAndDoesNotCreate()
    {
        // Alternate path 5أ: the email already belongs to an account.
        var existing = new User { Id = Guid.NewGuid(), Email = "taken@intellilect.io" };
        var users = new FakeUserRepositoryForAdmins(existingByEmail: existing);
        var admins = new FakeAdminRepository(adminById: null, adminRole: Role.Create(RoleName.Admin));
        var sut = CreateSut(admins, users);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.CreateAdminAsync(ValidCreateRequest() with { Email = existing.Email }));

        Assert.Null(users.Added);
        Assert.Equal(0, users.SaveChangesCallCount);
    }

    [Theory]
    [InlineData("", "admin@intellilect.io", "password123")]       // missing username
    [InlineData("adminuser", "not-an-email", "password123")]      // malformed email
    [InlineData("adminuser", "admin@intellilect.io", "short")]    // password too short
    public async Task CreateAdminAsync_WithInvalidData_ThrowsAndDoesNotCreate(
        string userName, string email, string password)
    {
        // Alternate path 5ب: missing/invalid input is rejected before any persistence.
        var users = new FakeUserRepositoryForAdmins(existingByEmail: null);
        var admins = new FakeAdminRepository(adminById: null, adminRole: Role.Create(RoleName.Admin));
        var sut = CreateSut(admins, users);

        var request = new CreateAdminRequest(userName, email, "First", "Last", password, Bio: null);

        await Assert.ThrowsAsync<ArgumentException>(() => sut.CreateAdminAsync(request));

        Assert.Null(users.Added);
        Assert.Equal(0, users.SaveChangesCallCount);
    }

    // ----- DeactivateAdminAsync ------------------------------------------------

    [Fact]
    public async Task DeactivateAdminAsync_WithExistingAdmin_DeactivatesAndNotifies()
    {
        // Main path: an active admin is deactivated and the owner is notified.
        var admin = ActiveAdmin();
        var users = new FakeUserRepositoryForAdmins(existingByEmail: null);
        var admins = new FakeAdminRepository(adminById: admin, adminRole: null);
        var eventBus = new CapturingEventBus();
        var sut = CreateSut(admins, users, eventBus);

        await sut.DeactivateAdminAsync(admin.Id, SuperAdminId);

        Assert.Equal(UserStatus.Deactivated, admin.Status);
        Assert.Equal(1, users.SaveChangesCallCount);
        var message = Assert.Single(eventBus.Published.OfType<UserStatusChangedMessage>());
        Assert.Equal(admin.Email, message.Email);
        Assert.Equal(UserStatus.Deactivated.ToString(), message.Status);
    }

    [Fact]
    public async Task DeactivateAdminAsync_WhenAdminNotFound_ThrowsNotFound()
    {
        // Alternate path 5ج: the target admin does not exist.
        var users = new FakeUserRepositoryForAdmins(existingByEmail: null);
        var admins = new FakeAdminRepository(adminById: null, adminRole: null);
        var eventBus = new CapturingEventBus();
        var sut = CreateSut(admins, users, eventBus);

        await Assert.ThrowsAsync<NotFoundException>(
            () => sut.DeactivateAdminAsync(Guid.NewGuid(), SuperAdminId));

        Assert.Equal(0, users.SaveChangesCallCount);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task DeactivateAdminAsync_WhenTargetIsSelf_ThrowsAndDoesNotTouchRepository()
    {
        // Alternate path 5د: the super admin must not deactivate their own account.
        var users = new FakeUserRepositoryForAdmins(existingByEmail: null);
        var admins = new FakeAdminRepository(adminById: null, adminRole: null);
        var eventBus = new CapturingEventBus();
        var sut = CreateSut(admins, users, eventBus);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.DeactivateAdminAsync(SuperAdminId, SuperAdminId));

        // The self-check happens before any lookup or write.
        Assert.False(admins.GetAdminByIdCalled);
        Assert.Equal(0, users.SaveChangesCallCount);
        Assert.Empty(eventBus.Published);
    }

    // ----- ReactivateAdminAsync ------------------------------------------------

    [Fact]
    public async Task ReactivateAdminAsync_WithDeactivatedAdmin_ReactivatesAndNotifies()
    {
        // Main path: a deactivated admin is reactivated and notified.
        var admin = ActiveAdmin();
        admin.Deactivate();
        var users = new FakeUserRepositoryForAdmins(existingByEmail: null);
        var admins = new FakeAdminRepository(adminById: admin, adminRole: null);
        var eventBus = new CapturingEventBus();
        var sut = CreateSut(admins, users, eventBus);

        await sut.ReactivateAdminAsync(admin.Id, SuperAdminId);

        Assert.Equal(UserStatus.Active, admin.Status);
        Assert.Equal(1, users.SaveChangesCallCount);
        var message = Assert.Single(eventBus.Published.OfType<UserStatusChangedMessage>());
        Assert.Equal(UserStatus.Active.ToString(), message.Status);
    }

    [Fact]
    public async Task ReactivateAdminAsync_WhenTargetIsSelf_Throws()
    {
        // Alternate path 5د: symmetrical guard for reactivation.
        var users = new FakeUserRepositoryForAdmins(existingByEmail: null);
        var admins = new FakeAdminRepository(adminById: null, adminRole: null);
        var sut = CreateSut(admins, users);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ReactivateAdminAsync(SuperAdminId, SuperAdminId));

        Assert.False(admins.GetAdminByIdCalled);
        Assert.Equal(0, users.SaveChangesCallCount);
    }

    // ----- helpers -------------------------------------------------------------

    private static SuperAdminService CreateSut(
        FakeAdminRepository admins,
        FakeUserRepositoryForAdmins users,
        CapturingEventBus? eventBus = null)
        => new(
            admins,
            users,
            new PassthroughHasher(),
            mapper: null!,
            eventBus ?? new CapturingEventBus());

    private static CreateAdminRequest ValidCreateRequest() =>
        new("adminuser", "admin@intellilect.io", "Ad", "Min", "password123", Bio: null);

    private static User ActiveAdmin()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = "adminuser",
            Email = "admin@intellilect.io",
            FirstName = "Ad",
            LastName = "Min",
            PasswordHash = "H:password123",
            RoleId = Guid.NewGuid(),
            Role = Role.Create(RoleName.Admin),
        };
        user.Approve();
        return user;
    }
}

// ----- fakes ------------------------------------------------------------------

internal sealed class FakeAdminRepository : IAdminRepository
{
    private readonly User? _adminById;
    private readonly Role? _adminRole;

    public FakeAdminRepository(User? adminById, Role? adminRole)
    {
        _adminById = adminById;
        _adminRole = adminRole;
    }

    public bool GetAdminByIdCalled { get; private set; }

    public Task<User?> GetAdminByIdAsync(Guid adminId, CancellationToken ct = default)
    {
        GetAdminByIdCalled = true;
        return Task.FromResult(_adminById);
    }

    public Task<Role?> GetAdminRoleAsync(CancellationToken ct = default) => Task.FromResult(_adminRole);

    public Task<(List<User> Items, int TotalCount)> GetAdminsAsync(AdminQuerySpecification specification, CancellationToken ct = default)
        => throw new NotImplementedException();
}

internal sealed class FakeUserRepositoryForAdmins : IUserRepository
{
    private readonly User? _existingByEmail;

    public FakeUserRepositoryForAdmins(User? existingByEmail) => _existingByEmail = existingByEmail;

    public User? Added { get; private set; }
    public User? Updated { get; private set; }
    public int SaveChangesCallCount { get; private set; }

    public Task<User?> FindByEmail(string email, CancellationToken cancellationToken = default) => Task.FromResult(_existingByEmail);

    public Task AddAsync(User entity, CancellationToken cancellationToken = default)
    {
        Added = entity;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(User entity, CancellationToken cancellationToken = default)
    {
        Updated = entity;
        return Task.CompletedTask;
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveChangesCallCount++;
        return Task.FromResult(1);
    }

    public Task<User?> FindByRefreshToken(string token, CancellationToken ct) => throw new NotImplementedException();
    public Task<User?> FindByResetToken(string token, CancellationToken ct) => throw new NotImplementedException();
    public Task<(List<User> Items, int TotalCount)> GetPaginatedPendingUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct) => throw new NotImplementedException();
    public Task<(List<User> Items, int TotalCount)> GetPaginatedUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct) => throw new NotImplementedException();
    public Task<(List<User> Items, int TotalCount)> SearchUsersAsync(UserManagementService.Application.Common.Users.UserQuerySpecification specification, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
}

internal sealed class PassthroughHasher : IHasher
{
    public string Hash(string code) => $"H:{code}";
    public bool Verify(string oldCode, string codeHash) => codeHash == $"H:{oldCode}";
}

internal sealed class CapturingEventBus : IEventBus
{
    public List<object> Published { get; } = new();

    public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class
    {
        Published.Add(message);
        return Task.CompletedTask;
    }
}
