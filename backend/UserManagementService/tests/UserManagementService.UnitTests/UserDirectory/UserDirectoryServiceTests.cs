using AutoMapper;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;
using UserManagementService.Application.Common.Users;
using UserManagementService.Application.DTOs.User;
using UserManagementService.Application.UserDirectory;
using UserManagementService.Domain.Entities;

namespace UserManagementService.UnitTests.UserDirectory;

// Unit tests for UserDirectoryService, covering the "البحث في المستخدمين وعرض تفاصيلهم" use-case:
//   SearchUsersAsync    -> steps 3-5 (paged results) + alternate path 5أ (no matches).
//   GetUserDetailAsync  -> steps 6-7 (profile + memberships)
//                          + 7أ (user not found) + 7ب (memberships fetch failed -> degrade).
public class UserDirectoryServiceTests
{
    // ----- SearchUsersAsync ----------------------------------------------------

    [Fact]
    public async Task SearchUsersAsync_ReturnsMappedPagedResults()
    {
        var users = new List<User> { ActiveUser(RoleName.Student), ActiveUser(RoleName.Teacher) };
        var repo = new FakeUserDirectoryRepository(searchResult: (users, 2));
        var sut = CreateSut(repo, new FakeClassroomClient());

        var request = new SearchUsersRequest { Page = 1, PageSize = 20 };
        var result = await sut.SearchUsersAsync(request);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(1, result.PageNumber);
        // Mapping exposes the role name as a string (via UserProfile).
        Assert.Contains(result.Items, i => i.RoleName == RoleName.Student.ToString());
    }

    [Fact]
    public async Task SearchUsersAsync_WithNoMatches_ReturnsEmptyPage()
    {
        // Alternate path 5أ: no users match — an empty page, not an error.
        var repo = new FakeUserDirectoryRepository(searchResult: (new List<User>(), 0));
        var sut = CreateSut(repo, new FakeClassroomClient());

        var result = await sut.SearchUsersAsync(new SearchUsersRequest { SearchTerm = "zzz-nobody" });

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task SearchUsersAsync_WithInvalidSort_ThrowsArgument()
    {
        // The specification validates input before the repository is touched.
        var repo = new FakeUserDirectoryRepository(searchResult: (new List<User>(), 0));
        var sut = CreateSut(repo, new FakeClassroomClient());

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.SearchUsersAsync(new SearchUsersRequest { SortBy = "not-a-field" }));
    }

    // ----- GetUserDetailAsync --------------------------------------------------

    [Fact]
    public async Task GetUserDetailAsync_WhenUserExists_ReturnsProfileWithMemberships()
    {
        var user = ActiveUser(RoleName.Teacher);
        var memberships = new UserClassrooms(
            new[] { Classroom("Algebra") },
            new[] { Classroom("History") });
        var repo = new FakeUserDirectoryRepository(userById: user);
        var client = new FakeClassroomClient(result: memberships);
        var sut = CreateSut(repo, client);

        var detail = await sut.GetUserDetailAsync(user.Id);

        Assert.Equal(user.Id, detail.User.Id);
        Assert.False(detail.MembershipsUnavailable);
        Assert.Single(detail.Teaching);
        Assert.Single(detail.Enrolled);
        Assert.Equal("Algebra", detail.Teaching[0].Name);
    }

    [Fact]
    public async Task GetUserDetailAsync_WhenUserNotFound_ThrowsNotFound()
    {
        // Alternate path 7أ: the account does not exist.
        var repo = new FakeUserDirectoryRepository(userById: null);
        var client = new FakeClassroomClient();
        var sut = CreateSut(repo, client);

        await Assert.ThrowsAsync<NotFoundException>(() => sut.GetUserDetailAsync(Guid.NewGuid()));

        // The cross-service call must not run for a non-existent user.
        Assert.False(client.WasCalled);
    }

    [Fact]
    public async Task GetUserDetailAsync_WhenClassroomClientFails_ReturnsProfileWithoutMemberships()
    {
        // Alternate path 7ب: memberships could not be fetched -> return the profile, flag it.
        var user = ActiveUser(RoleName.Student);
        var repo = new FakeUserDirectoryRepository(userById: user);
        var client = new FakeClassroomClient(throws: new HttpRequestException("classroom-service down"));
        var sut = CreateSut(repo, client);

        var detail = await sut.GetUserDetailAsync(user.Id);

        Assert.Equal(user.Id, detail.User.Id);
        Assert.True(detail.MembershipsUnavailable);
        Assert.Empty(detail.Teaching);
        Assert.Empty(detail.Enrolled);
    }

    // ----- helpers -------------------------------------------------------------

    private static UserDirectoryService CreateSut(FakeUserDirectoryRepository repo, FakeClassroomClient client)
        => new(repo, client, BuildMapper());

    private static IMapper BuildMapper()
        => new MapperConfiguration(cfg => cfg.AddProfile<UserProfile>()).CreateMapper();

    private static User ActiveUser(RoleName role)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = role.ToString().ToLowerInvariant(),
            Email = $"{role}@intellilect.io".ToLowerInvariant(),
            FirstName = "First",
            LastName = "Last",
            PasswordHash = "H:pass",
            RoleId = Guid.NewGuid(),
            Role = Role.Create(role),
        };
        user.Approve();
        return user;
    }

    private static ClassroomSummary Classroom(string name) =>
        new(Guid.NewGuid(), name, $"{name} description", Guid.NewGuid(), DateTime.UtcNow, FileCount: 3, StudentCount: 12);
}

// ----- fakes ------------------------------------------------------------------

internal sealed class FakeUserDirectoryRepository : IUserRepository
{
    private readonly (List<User> Items, int TotalCount) _searchResult;
    private readonly User? _userById;

    public FakeUserDirectoryRepository(
        (List<User> Items, int TotalCount)? searchResult = null,
        User? userById = null)
    {
        _searchResult = searchResult ?? (new List<User>(), 0);
        _userById = userById;
    }

    public Task<(List<User> Items, int TotalCount)> SearchUsersAsync(UserQuerySpecification specification, CancellationToken ct = default)
        => Task.FromResult(_searchResult);

    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_userById);

    public Task<User?> FindByEmail(string email, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<User?> GetByIdWithRefreshTokensAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<User>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<User?> FindByRefreshToken(string token, CancellationToken ct) => throw new NotImplementedException();
    public Task<User?> FindByResetToken(string token, CancellationToken ct) => throw new NotImplementedException();
    public Task<(List<User> Items, int TotalCount)> GetPaginatedPendingUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct) => throw new NotImplementedException();
    public Task<(List<User> Items, int TotalCount)> GetPaginatedUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct) => throw new NotImplementedException();
    public Task AddAsync(User entity, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task UpdateAsync(User entity, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
}

internal sealed class FakeClassroomClient : IClassroomInternalClient
{
    private readonly UserClassrooms _result;
    private readonly Exception? _throws;

    public FakeClassroomClient(UserClassrooms? result = null, Exception? throws = null)
    {
        _result = result ?? UserClassrooms.Empty;
        _throws = throws;
    }

    public bool WasCalled { get; private set; }

    // Admin classroom operations are not exercised by the user-directory use-case.
    public Task<AdminClassroomPage> GetClassroomsAsync(int page, int pageSize, string? search, Guid? teacherId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AdminClassroom?> GetClassroomByIdAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Guid> CreateClassroomAsync(Guid teacherId, string name, string description, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpdateClassroomAsync(Guid id, string name, string description, long version, CancellationToken ct = default) => throw new NotImplementedException();

    public Task<UserClassrooms> GetUserClassroomsAsync(Guid userId, CancellationToken ct = default)
    {
        WasCalled = true;
        if (_throws is not null)
        {
            return Task.FromException<UserClassrooms>(_throws);
        }

        return Task.FromResult(_result);
    }
}
