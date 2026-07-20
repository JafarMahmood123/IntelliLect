using UserManagementService.Application.Abstractions;
using UserManagementService.Application.ClassroomAdministration;
using UserManagementService.Application.Common;
using UserManagementService.Application.DTOs.Classroom;
using UserManagementService.Domain.Entities;

namespace UserManagementService.UnitTests.ClassroomAdministration;

// Unit tests for ClassroomAdminService, mirroring the "استعراض الفصول الدراسية وإدارتها" use-case:
//   GetClassroomsAsync -> steps 3-5 (list) + teacher-name enrichment.
//   CreateClassroomAsync -> steps 5-6 + 5أ (invalid data) + 5ب (invalid teacher).
//   UpdateClassroomAsync -> 5أ + 5ج (not found) + 6أ (concurrency), delegated to the client.
public class ClassroomAdminServiceTests
{
    // ----- listing + enrichment ------------------------------------------------

    [Fact]
    public async Task GetClassrooms_EnrichesTeacherNamesFromLocalUsers()
    {
        var t1 = Teacher("Ada", "Byron");
        var t2 = Teacher("Alan", "Turing");
        var page = Page(
            Classroom("Math", t1.Id),
            Classroom("CS", t2.Id));
        var client = new FakeClassroomClient(page);
        var users = new FakeClassroomUserRepository(t1, t2);
        var sut = new ClassroomAdminService(client, users);

        var result = await sut.GetClassroomsAsync(new SearchClassroomsRequest { Page = 1, PageSize = 20 });

        Assert.Equal(2, result.Items.Count);
        var math = result.Items.Single(i => i.Name == "Math");
        Assert.Equal("Ada Byron", math.TeacherName);
        Assert.Equal(t1.Email, math.TeacherEmail);
        Assert.Equal(2, result.TotalCount);
    }

    [Fact]
    public async Task GetClassrooms_WhenTeacherUnknown_LeavesTeacherNameNull()
    {
        var page = Page(Classroom("Orphan", Guid.NewGuid()));
        var client = new FakeClassroomClient(page);
        var users = new FakeClassroomUserRepository(); // no matching teacher
        var sut = new ClassroomAdminService(client, users);

        var result = await sut.GetClassroomsAsync(new SearchClassroomsRequest());

        var item = Assert.Single(result.Items);
        Assert.Null(item.TeacherName);
        Assert.Null(item.TeacherEmail);
    }

    // ----- create --------------------------------------------------------------

    [Fact]
    public async Task Create_WithValidActiveTeacher_CallsClientAndReturnsId()
    {
        var teacher = Teacher("Grace", "Hopper");
        var client = new FakeClassroomClient(createdId: Guid.NewGuid());
        var users = new FakeClassroomUserRepository(teacher);
        var sut = new ClassroomAdminService(client, users);

        var request = new CreateClassroomAdminRequest(teacher.Id, "  Compilers  ", "  Intro  ");
        var id = await sut.CreateClassroomAsync(request);

        Assert.Equal(client.CreatedId, id);
        Assert.Equal(teacher.Id, client.CreatedTeacherId);
        Assert.Equal("Compilers", client.CreatedName);   // trimmed
        Assert.Equal("Intro", client.CreatedDescription);
    }

    [Theory]
    [InlineData("", "desc")]     // missing name
    [InlineData("name", "")]     // missing description
    public async Task Create_WithInvalidData_ThrowsAndDoesNotCallClient(string name, string description)
    {
        // Alternate path 5أ.
        var teacher = Teacher("Grace", "Hopper");
        var client = new FakeClassroomClient(createdId: Guid.NewGuid());
        var users = new FakeClassroomUserRepository(teacher);
        var sut = new ClassroomAdminService(client, users);

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.CreateClassroomAsync(new CreateClassroomAdminRequest(teacher.Id, name, description)));

        Assert.False(client.CreateCalled);
    }

    [Fact]
    public async Task Create_WhenTeacherDoesNotExist_ThrowsAndDoesNotCallClient()
    {
        // Alternate path 5ب.
        var client = new FakeClassroomClient(createdId: Guid.NewGuid());
        var users = new FakeClassroomUserRepository(); // empty
        var sut = new ClassroomAdminService(client, users);

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.CreateClassroomAsync(new CreateClassroomAdminRequest(Guid.NewGuid(), "Name", "Desc")));

        Assert.False(client.CreateCalled);
    }

    [Fact]
    public async Task Create_WhenAssignedUserIsNotATeacher_Throws()
    {
        // Alternate path 5ب: wrong role.
        var student = ActiveUser("Sam", "Student", RoleName.Student);
        var client = new FakeClassroomClient(createdId: Guid.NewGuid());
        var users = new FakeClassroomUserRepository(student);
        var sut = new ClassroomAdminService(client, users);

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.CreateClassroomAsync(new CreateClassroomAdminRequest(student.Id, "Name", "Desc")));

        Assert.False(client.CreateCalled);
    }

    [Fact]
    public async Task Create_WhenTeacherAccountNotActive_Throws()
    {
        // Alternate path 5ب: inactive teacher.
        var teacher = ActiveUser("Grace", "Hopper", RoleName.Teacher);
        teacher.Deactivate();
        var client = new FakeClassroomClient(createdId: Guid.NewGuid());
        var users = new FakeClassroomUserRepository(teacher);
        var sut = new ClassroomAdminService(client, users);

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.CreateClassroomAsync(new CreateClassroomAdminRequest(teacher.Id, "Name", "Desc")));

        Assert.False(client.CreateCalled);
    }

    // ----- update --------------------------------------------------------------

    [Fact]
    public async Task Update_WithValidData_CallsClientWithVersion()
    {
        var client = new FakeClassroomClient();
        var sut = new ClassroomAdminService(client, new FakeClassroomUserRepository());
        var id = Guid.NewGuid();

        await sut.UpdateClassroomAsync(id, new UpdateClassroomAdminRequest("New name", "New desc", 42));

        Assert.True(client.UpdateCalled);
        Assert.Equal(id, client.UpdatedId);
        Assert.Equal("New name", client.UpdatedName);
        Assert.Equal(42, client.UpdatedVersion);
    }

    [Fact]
    public async Task Update_WithMissingName_ThrowsAndDoesNotCallClient()
    {
        // Alternate path 5أ.
        var client = new FakeClassroomClient();
        var sut = new ClassroomAdminService(client, new FakeClassroomUserRepository());

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.UpdateClassroomAsync(Guid.NewGuid(), new UpdateClassroomAdminRequest("", "desc", 1)));

        Assert.False(client.UpdateCalled);
    }

    [Fact]
    public async Task Update_WhenClassroomNotFound_PropagatesNotFound()
    {
        // Alternate path 5ج: the client surfaces a 404 as NotFoundException.
        var client = new FakeClassroomClient(updateThrows: new NotFoundException("Classroom not found."));
        var sut = new ClassroomAdminService(client, new FakeClassroomUserRepository());

        await Assert.ThrowsAsync<NotFoundException>(
            () => sut.UpdateClassroomAsync(Guid.NewGuid(), new UpdateClassroomAdminRequest("N", "D", 1)));
    }

    [Fact]
    public async Task Update_WhenConcurrentModification_PropagatesConflict()
    {
        // Alternate path 6أ: the client surfaces a 409 as InvalidOperationException.
        var client = new FakeClassroomClient(updateThrows: new InvalidOperationException("modified concurrently"));
        var sut = new ClassroomAdminService(client, new FakeClassroomUserRepository());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.UpdateClassroomAsync(Guid.NewGuid(), new UpdateClassroomAdminRequest("N", "D", 1)));
    }

    // ----- helpers -------------------------------------------------------------

    private static AdminClassroom Classroom(string name, Guid teacherId) =>
        new(Guid.NewGuid(), name, $"{name} desc", teacherId, DateTime.UtcNow, FileCount: 1, StudentCount: 2, SessionCount: 3, Version: 10);

    private static AdminClassroomPage Page(params AdminClassroom[] items) =>
        new(items, items.Length, 1, 20, 1);

    private static User Teacher(string first, string last) => ActiveUser(first, last, RoleName.Teacher);

    private static User ActiveUser(string first, string last, RoleName role)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = $"{first}.{last}".ToLowerInvariant(),
            Email = $"{first}.{last}@intellilect.io".ToLowerInvariant(),
            FirstName = first,
            LastName = last,
            PasswordHash = "H:pass",
            RoleId = Guid.NewGuid(),
            Role = Role.Create(role),
        };
        user.Approve();
        return user;
    }
}

// ----- fakes ------------------------------------------------------------------

internal sealed class FakeClassroomClient : IClassroomInternalClient
{
    private readonly AdminClassroomPage _page;
    private readonly Exception? _updateThrows;

    public FakeClassroomClient(AdminClassroomPage? page = null, Guid? createdId = null, Exception? updateThrows = null)
    {
        _page = page ?? new AdminClassroomPage(Array.Empty<AdminClassroom>(), 0, 1, 20, 0);
        CreatedId = createdId ?? Guid.NewGuid();
        _updateThrows = updateThrows;
    }

    public Guid CreatedId { get; }
    public bool CreateCalled { get; private set; }
    public Guid CreatedTeacherId { get; private set; }
    public string? CreatedName { get; private set; }
    public string? CreatedDescription { get; private set; }

    public bool UpdateCalled { get; private set; }
    public Guid UpdatedId { get; private set; }
    public string? UpdatedName { get; private set; }
    public long UpdatedVersion { get; private set; }

    public Task<AdminClassroomPage> GetClassroomsAsync(int page, int pageSize, string? search, Guid? teacherId, CancellationToken ct = default)
        => Task.FromResult(_page);

    public Task<Guid> CreateClassroomAsync(Guid teacherId, string name, string description, CancellationToken ct = default)
    {
        CreateCalled = true;
        CreatedTeacherId = teacherId;
        CreatedName = name;
        CreatedDescription = description;
        return Task.FromResult(CreatedId);
    }

    public Task UpdateClassroomAsync(Guid id, string name, string description, long version, CancellationToken ct = default)
    {
        UpdateCalled = true;
        UpdatedId = id;
        UpdatedName = name;
        UpdatedVersion = version;
        return _updateThrows is not null ? Task.FromException(_updateThrows) : Task.CompletedTask;
    }

    public Task<AdminClassroom?> GetClassroomByIdAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<UserClassrooms> GetUserClassroomsAsync(Guid userId, CancellationToken ct = default) => throw new NotImplementedException();
}

internal sealed class FakeClassroomUserRepository : IUserRepository
{
    private readonly List<User> _users;
    public FakeClassroomUserRepository(params User[] users) => _users = users.ToList();

    public Task<List<User>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        => Task.FromResult(_users.Where(u => ids.Contains(u.Id)).ToList());

    public Task<User?> FindByEmail(string email, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<User?> GetByIdWithRefreshTokensAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<User?> FindByRefreshToken(string token, CancellationToken ct) => throw new NotImplementedException();
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
