using IntelliLect.Contracts.Messages;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;
using UserManagementService.Application.DTOs.Member;
using UserManagementService.Application.MemberAdministration;
using UserManagementService.Domain.Entities;

namespace UserManagementService.UnitTests.MemberAdministration;

// Unit tests for ClassroomMemberAdminService — the super-admin member management gateway.
//   List -> enrich (teacher + students) + search + page. Add -> 5ب (active student), 5ج no-op (no notify).
//   Remove -> 4أ (reason), propagates 5أ/5د (NotFound) and 5هـ (InvalidOperation). Notify is best-effort.
public class ClassroomMemberAdminServiceTests
{
    private static readonly Guid ClassroomId = Guid.NewGuid();

    // ----- list ----------------------------------------------------------------

    [Fact]
    public async Task GetMembers_EnrichesAndPinsTeacherFirst()
    {
        var teacher = User("Ada", "Byron", RoleName.Teacher);
        var s1 = User("Grace", "Hopper", RoleName.Student);
        var s2 = User("Alan", "Turing", RoleName.Student);
        var client = new FakeMemberClient
        {
            Members = new ClassroomMembersData(ClassroomId, "Physics", teacher.Id, new[]
            {
                new ClassroomMemberRow(s1.Id, DateTime.UtcNow.AddDays(-2)),
                new ClassroomMemberRow(s2.Id, DateTime.UtcNow.AddDays(-1)),
            }),
        };
        var sut = new ClassroomMemberAdminService(client, new FakeMemberUserRepository(teacher, s1, s2), new FakeMemberNotificationBus());

        var result = await sut.GetMembersAsync(ClassroomId, new SearchMembersRequest());

        Assert.Equal(3, result.TotalCount);
        var first = result.Items[0];
        Assert.True(first.IsTeacher);
        Assert.Equal("Ada Byron", first.Name);
        Assert.Equal("Teacher", first.RoleInClass);
        Assert.Null(first.JoinedAtUtc);
        Assert.Contains(result.Items, i => i.Name == "Grace Hopper" && i.RoleInClass == "Student");
    }

    [Fact]
    public async Task GetMembers_FiltersBySearchAcrossNameAndEmail()
    {
        var teacher = User("Ada", "Byron", RoleName.Teacher);
        var s1 = User("Grace", "Hopper", RoleName.Student);
        var s2 = User("Alan", "Turing", RoleName.Student);
        var client = new FakeMemberClient
        {
            Members = new ClassroomMembersData(ClassroomId, "Physics", teacher.Id, new[]
            {
                new ClassroomMemberRow(s1.Id, DateTime.UtcNow),
                new ClassroomMemberRow(s2.Id, DateTime.UtcNow),
            }),
        };
        var sut = new ClassroomMemberAdminService(client, new FakeMemberUserRepository(teacher, s1, s2), new FakeMemberNotificationBus());

        var result = await sut.GetMembersAsync(ClassroomId, new SearchMembersRequest { Search = "Hopper" });

        var only = Assert.Single(result.Items);
        Assert.Equal("Grace Hopper", only.Name);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task GetMembers_WhenClassroomMissing_PropagatesNotFound()
    {
        var client = new FakeMemberClient { MembersThrows = new NotFoundException("Classroom not found.") };
        var sut = new ClassroomMemberAdminService(client, new FakeMemberUserRepository(), new FakeMemberNotificationBus());

        await Assert.ThrowsAsync<NotFoundException>(() => sut.GetMembersAsync(ClassroomId, new SearchMembersRequest())); // 5أ
    }

    // ----- add -----------------------------------------------------------------

    [Fact]
    public async Task AddMember_WhenTargetNotActiveStudent_ThrowsAndDoesNotCallClient()
    {
        // 5ب: a non-student (here a teacher) is rejected before the cross-service call.
        var teacher = User("Ada", "Byron", RoleName.Teacher);
        var client = new FakeMemberClient();
        var sut = new ClassroomMemberAdminService(client, new FakeMemberUserRepository(teacher), new FakeMemberNotificationBus());

        await Assert.ThrowsAsync<ArgumentException>(() => sut.AddMemberAsync(ClassroomId, new AddMemberRequest(teacher.Id)));
        Assert.False(client.AddCalled);
    }

    [Fact]
    public async Task AddMember_HappyPath_AddsAndNotifies()
    {
        var student = User("Grace", "Hopper", RoleName.Student);
        var client = new FakeMemberClient
        {
            AddResult = new MemberChangeResult(true, ClassroomId, "Physics", student.Id),
        };
        var bus = new FakeMemberNotificationBus();
        var sut = new ClassroomMemberAdminService(client, new FakeMemberUserRepository(student), bus);

        var result = await sut.AddMemberAsync(ClassroomId, new AddMemberRequest(student.Id));

        Assert.True(client.AddCalled);
        Assert.True(result.Changed);
        Assert.Equal("Added", result.Action);
        var msg = Assert.Single(bus.Published);
        Assert.Equal(student.Email, msg.Email);
        Assert.True(msg.IsAdded);
        Assert.Equal("Physics", msg.ClassroomName);
    }

    [Fact]
    public async Task AddMember_WhenAlreadyMember_DoesNotNotify()
    {
        // 5ج: ClassroomService reports Changed=false (already a member).
        var student = User("Grace", "Hopper", RoleName.Student);
        var client = new FakeMemberClient
        {
            AddResult = new MemberChangeResult(false, ClassroomId, "Physics", student.Id),
        };
        var bus = new FakeMemberNotificationBus();
        var sut = new ClassroomMemberAdminService(client, new FakeMemberUserRepository(student), bus);

        var result = await sut.AddMemberAsync(ClassroomId, new AddMemberRequest(student.Id));

        Assert.False(result.Changed);
        Assert.Empty(bus.Published);
    }

    // ----- remove --------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RemoveMember_WithoutReason_ThrowsAndDoesNotCallClient(string reason)
    {
        var client = new FakeMemberClient();
        var sut = new ClassroomMemberAdminService(client, new FakeMemberUserRepository(), new FakeMemberNotificationBus());

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.RemoveMemberAsync(ClassroomId, Guid.NewGuid(), new RemoveMemberRequest(reason))); // 4أ
        Assert.False(client.RemoveCalled);
    }

    [Fact]
    public async Task RemoveMember_HappyPath_RemovesAndNotifies()
    {
        var student = User("Grace", "Hopper", RoleName.Student);
        var client = new FakeMemberClient
        {
            RemoveResult = new MemberChangeResult(true, ClassroomId, "Physics", student.Id),
        };
        var bus = new FakeMemberNotificationBus();
        var sut = new ClassroomMemberAdminService(client, new FakeMemberUserRepository(student), bus);

        var result = await sut.RemoveMemberAsync(ClassroomId, student.Id, new RemoveMemberRequest("enrolled by mistake"));

        Assert.True(client.RemoveCalled);
        Assert.Equal("Removed", result.Action);
        var msg = Assert.Single(bus.Published);
        Assert.False(msg.IsAdded);
        Assert.Equal(student.Email, msg.Email);
    }

    [Fact]
    public async Task RemoveMember_WhenNotFound_PropagatesNotFound()
    {
        // 5أ / 5د: the client surfaces a 404 as NotFoundException.
        var client = new FakeMemberClient { RemoveThrows = new NotFoundException("Classroom or membership not found.") };
        var sut = new ClassroomMemberAdminService(client, new FakeMemberUserRepository(), new FakeMemberNotificationBus());

        await Assert.ThrowsAsync<NotFoundException>(
            () => sut.RemoveMemberAsync(ClassroomId, Guid.NewGuid(), new RemoveMemberRequest("cleanup")));
    }

    [Fact]
    public async Task RemoveMember_WhenTargetIsTeacher_PropagatesInvalidOperation()
    {
        // 5هـ: the client surfaces a 409 as InvalidOperationException.
        var client = new FakeMemberClient { RemoveThrows = new InvalidOperationException("teacher") };
        var sut = new ClassroomMemberAdminService(client, new FakeMemberUserRepository(), new FakeMemberNotificationBus());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.RemoveMemberAsync(ClassroomId, Guid.NewGuid(), new RemoveMemberRequest("cleanup")));
    }

    [Fact]
    public async Task RemoveMember_WhenNotificationFails_StillSucceeds()
    {
        var student = User("Grace", "Hopper", RoleName.Student);
        var client = new FakeMemberClient
        {
            RemoveResult = new MemberChangeResult(true, ClassroomId, "Physics", student.Id),
        };
        var bus = new FakeMemberNotificationBus { Throw = true };
        var sut = new ClassroomMemberAdminService(client, new FakeMemberUserRepository(student), bus);

        var result = await sut.RemoveMemberAsync(ClassroomId, student.Id, new RemoveMemberRequest("cleanup"));

        Assert.True(result.Changed); // a broker outage must not fail a completed removal
    }

    // ----- helpers -------------------------------------------------------------

    private static User User(string first, string last, RoleName role)
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

internal sealed class FakeMemberNotificationBus : INotificationBus
{
    public List<ClassroomMembershipChangedMessage> Published { get; } = new();
    public bool Throw { get; set; }

    public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class
    {
        if (Throw) throw new InvalidOperationException("broker down");
        if (message is ClassroomMembershipChangedMessage m) Published.Add(m);
        return Task.CompletedTask;
    }
}

internal sealed class FakeMemberUserRepository : IUserRepository
{
    private readonly List<User> _users;
    public FakeMemberUserRepository(params User[] users) => _users = users.ToList();

    public Task<List<User>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        => Task.FromResult(_users.Where(u => ids.Contains(u.Id)).ToList());
    public Task<List<User>> GetByIdsWithRefreshTokensAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => throw new NotImplementedException();

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

internal sealed class FakeMemberClient : IClassroomInternalClient
{
    public ClassroomMembersData Members { get; set; } = new(Guid.Empty, string.Empty, Guid.Empty, Array.Empty<ClassroomMemberRow>());
    public Exception? MembersThrows { get; set; }
    public MemberChangeResult AddResult { get; set; } = new(true, Guid.Empty, "Physics", Guid.Empty);
    public MemberChangeResult RemoveResult { get; set; } = new(true, Guid.Empty, "Physics", Guid.Empty);
    public Exception? RemoveThrows { get; set; }
    public bool AddCalled { get; private set; }
    public bool RemoveCalled { get; private set; }

    public Task<ClassroomMembersData> GetClassroomMembersAsync(Guid classroomId, CancellationToken ct = default)
        => MembersThrows is not null ? Task.FromException<ClassroomMembersData>(MembersThrows) : Task.FromResult(Members);

    public Task<MemberChangeResult> AddClassroomMemberAsync(Guid classroomId, Guid studentId, CancellationToken ct = default)
    {
        AddCalled = true;
        return Task.FromResult(AddResult);
    }

    public Task<MemberChangeResult> RemoveClassroomMemberAsync(Guid classroomId, Guid studentId, CancellationToken ct = default)
    {
        RemoveCalled = true;
        return RemoveThrows is not null ? Task.FromException<MemberChangeResult>(RemoveThrows) : Task.FromResult(RemoveResult);
    }

    // unused
    public Task<UserClassrooms> GetUserClassroomsAsync(Guid userId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AdminClassroomPage> GetClassroomsAsync(int page, int pageSize, string? search, Guid? teacherId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AdminClassroom?> GetClassroomByIdAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Guid> CreateClassroomAsync(Guid teacherId, string name, string description, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpdateClassroomAsync(Guid id, string name, string description, long version, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ClassroomTeacherChange> ChangeClassroomTeacherAsync(Guid id, Guid newTeacherId, long version, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ClassroomDeletionImpact?> GetClassroomDeletionImpactAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ClassroomDeletionResult> DeleteClassroomAsync(Guid id, string reason, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AdminSessionPage> GetSessionsAsync(int page, int pageSize, string? search, string? status, Guid? classroomId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ForceEndResult> ForceEndSessionAsync(Guid sessionId, string reason, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<SessionDeletionImpact?> GetSessionDeletionImpactAsync(Guid sessionId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<SessionDeletionResult> DeleteSessionAsync(Guid sessionId, string reason, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AdminFilePage> GetFilesAsync(int page, int pageSize, string? search, Guid? classroomId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<AdminFile>> GetFilesByIdsAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<ClassroomName>> GetClassroomNamesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<FileDeletionResult> DeleteFileAsync(Guid fileId, string reason, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AdminOutputPage> GetOutputsAsync(int page, int pageSize, string? search, string? type, string? status, Guid? classroomId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<OutputDeletionResult> DeleteRecordingAsync(Guid recordingId, string reason, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<OutputDeletionResult> DeleteSummaryAsync(Guid summaryId, string reason, CancellationToken ct = default) => throw new NotImplementedException();
}
