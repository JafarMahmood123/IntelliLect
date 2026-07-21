using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Classroom;
using ClassroomService.Application.Exceptions;
using ClassroomService.Application.Services;
using ClassroomService.Domain.Entities;

namespace ClassroomService.UnitTests;

// Unit tests for ClassroomMemberAdminService — the super-admin member management use-case
// ("إدارة أعضاء الفصل الدراسي"), the part ClassroomService owns:
//   5أ classroom missing, 5ج already a member (no-op), 5د membership missing, 5هـ target is the teacher.
public class ClassroomMemberAdminServiceTests
{
    private static readonly Guid ClassroomId = Guid.NewGuid();
    private static readonly Guid TeacherId = Guid.NewGuid();
    private static readonly Guid StudentId = Guid.NewGuid();

    // ----- list ----------------------------------------------------------------

    [Fact]
    public async Task GetMembers_WhenClassroomMissing_ThrowsKeyNotFound()
    {
        var sut = new ClassroomMemberAdminService(new FakeClassroomRepo(info: null), new FakeMembershipRepo());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => sut.GetMembersAsync(ClassroomId)); // 5أ
    }

    [Fact]
    public async Task GetMembers_ReturnsTeacherAndStudents()
    {
        var joined = DateTime.UtcNow.AddDays(-3);
        var repo = new FakeMembershipRepo();
        repo.Seed(ClassroomId, StudentId, joined);
        var sut = new ClassroomMemberAdminService(new FakeClassroomRepo(new ClassroomTeacherInfo(TeacherId, "Physics")), repo);

        var result = await sut.GetMembersAsync(ClassroomId);

        Assert.Equal(TeacherId, result.TeacherId);
        Assert.Equal("Physics", result.ClassroomName);
        var member = Assert.Single(result.Students);
        Assert.Equal(StudentId, member.StudentId);
        Assert.Equal(joined, member.JoinedAtUtc);
    }

    // ----- add -----------------------------------------------------------------

    [Fact]
    public async Task AddMember_WhenClassroomMissing_ThrowsKeyNotFound()
    {
        var sut = new ClassroomMemberAdminService(new FakeClassroomRepo(info: null), new FakeMembershipRepo());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => sut.AddMemberAsync(ClassroomId, StudentId)); // 5أ
    }

    [Fact]
    public async Task AddMember_HappyPath_EnrollsStudent()
    {
        var repo = new FakeMembershipRepo();
        var sut = new ClassroomMemberAdminService(new FakeClassroomRepo(new ClassroomTeacherInfo(TeacherId, "Physics")), repo);

        var result = await sut.AddMemberAsync(ClassroomId, StudentId);

        Assert.True(result.Changed);
        Assert.Equal("Physics", result.ClassroomName);
        Assert.True(repo.IsEnrolled(ClassroomId, StudentId));
        Assert.Equal(1, repo.SaveCount);
    }

    [Fact]
    public async Task AddMember_WhenAlreadyEnrolled_IsNoOp()
    {
        var repo = new FakeMembershipRepo();
        repo.Seed(ClassroomId, StudentId, DateTime.UtcNow);
        var sut = new ClassroomMemberAdminService(new FakeClassroomRepo(new ClassroomTeacherInfo(TeacherId, "Physics")), repo);

        var result = await sut.AddMemberAsync(ClassroomId, StudentId); // 5ج

        Assert.False(result.Changed);
        Assert.Equal(0, repo.AddCount); // no duplicate row created
    }

    [Fact]
    public async Task AddMember_WhenTargetIsTeacher_IsNoOp()
    {
        // The owner is already a member (as teacher) — adding them as a student is a no-op, not a duplicate.
        var repo = new FakeMembershipRepo();
        var sut = new ClassroomMemberAdminService(new FakeClassroomRepo(new ClassroomTeacherInfo(TeacherId, "Physics")), repo);

        var result = await sut.AddMemberAsync(ClassroomId, TeacherId);

        Assert.False(result.Changed);
        Assert.Equal(0, repo.AddCount);
    }

    // ----- remove --------------------------------------------------------------

    [Fact]
    public async Task RemoveMember_WhenClassroomMissing_ThrowsKeyNotFound()
    {
        var sut = new ClassroomMemberAdminService(new FakeClassroomRepo(info: null), new FakeMembershipRepo());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => sut.RemoveMemberAsync(ClassroomId, StudentId)); // 5أ
    }

    [Fact]
    public async Task RemoveMember_WhenTargetIsTeacher_ThrowsConflict()
    {
        var repo = new FakeMembershipRepo();
        var sut = new ClassroomMemberAdminService(new FakeClassroomRepo(new ClassroomTeacherInfo(TeacherId, "Physics")), repo);

        await Assert.ThrowsAsync<ConflictException>(() => sut.RemoveMemberAsync(ClassroomId, TeacherId)); // 5هـ
        Assert.Equal(0, repo.DeleteCount);
    }

    [Fact]
    public async Task RemoveMember_WhenMembershipMissing_ThrowsKeyNotFound()
    {
        var repo = new FakeMembershipRepo(); // no membership seeded
        var sut = new ClassroomMemberAdminService(new FakeClassroomRepo(new ClassroomTeacherInfo(TeacherId, "Physics")), repo);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => sut.RemoveMemberAsync(ClassroomId, StudentId)); // 5د
    }

    [Fact]
    public async Task RemoveMember_HappyPath_RemovesMembership()
    {
        var repo = new FakeMembershipRepo();
        repo.Seed(ClassroomId, StudentId, DateTime.UtcNow);
        var sut = new ClassroomMemberAdminService(new FakeClassroomRepo(new ClassroomTeacherInfo(TeacherId, "Physics")), repo);

        var result = await sut.RemoveMemberAsync(ClassroomId, StudentId);

        Assert.True(result.Changed);
        Assert.False(repo.IsEnrolled(ClassroomId, StudentId));
        Assert.Equal(1, repo.DeleteCount);
    }

    // ----- fakes ---------------------------------------------------------------

    private sealed class FakeClassroomRepo : IClassroomRepository
    {
        private readonly ClassroomTeacherInfo? _info;
        public FakeClassroomRepo(ClassroomTeacherInfo? info) => _info = info;

        public Task<ClassroomTeacherInfo?> GetTeacherInfoAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_info);

        // unused
        public Task<Classroom?> GetWithDetailsAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<Classroom>> GetByTeacherIdAsync(Guid teacherId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<Classroom>> GetEnrolledClassroomsAsync(Guid studentId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(List<AdminClassroomResponse> Items, int TotalCount)> GetAdminPagedAsync(int page, int pageSize, string? search, Guid? teacherId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<AdminClassroomResponse?> GetAdminByIdAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> UpdateWithConcurrencyAsync(Guid id, string name, string description, long expectedVersion, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> HasLiveSessionAsync(Guid classroomId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ChangeTeacherWithConcurrencyAsync(Guid id, Guid newTeacherId, long expectedVersion, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<(Guid Id, string Name)>> GetNamesByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(IEnumerable<Classroom> Items, int TotalCount)> GetPagedAsync(int page, int pageSize, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Classroom?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AddAsync(Classroom entity, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateAsync(Classroom entity, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> SaveChangesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeMembershipRepo : IMembershipRepository
    {
        private readonly List<ClassroomMembership> _store = new();
        public int AddCount { get; private set; }
        public int DeleteCount { get; private set; }
        public int SaveCount { get; private set; }

        public void Seed(Guid classroomId, Guid studentId, DateTime joinedAtUtc)
            => _store.Add(new ClassroomMembership { Id = Guid.NewGuid(), ClassroomId = classroomId, StudentId = studentId, JoinedAtUtc = joinedAtUtc });

        public bool IsEnrolled(Guid classroomId, Guid studentId)
            => _store.Any(m => m.ClassroomId == classroomId && m.StudentId == studentId);

        public Task<bool> IsEnrolledAsync(Guid classroomId, Guid studentId, CancellationToken ct = default)
            => Task.FromResult(IsEnrolled(classroomId, studentId));

        public Task<List<ClassroomMembership>> GetMembersWithDetailsAsync(Guid classroomId, CancellationToken ct = default)
            => Task.FromResult(_store.Where(m => m.ClassroomId == classroomId).OrderBy(m => m.JoinedAtUtc).ToList());

        public Task<ClassroomMembership?> GetMembershipAsync(Guid classroomId, Guid studentId, CancellationToken ct = default)
            => Task.FromResult(_store.FirstOrDefault(m => m.ClassroomId == classroomId && m.StudentId == studentId));

        public Task AddAsync(ClassroomMembership entity, CancellationToken ct = default)
        {
            AddCount++;
            _store.Add(entity);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
        {
            DeleteCount++;
            _store.RemoveAll(m => m.Id == id);
            return Task.CompletedTask;
        }

        public Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            SaveCount++;
            return Task.FromResult(1);
        }

        // unused
        public Task<(IEnumerable<ClassroomMembership> Items, int TotalCount)> GetPagedAsync(int page, int pageSize, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ClassroomMembership?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateAsync(ClassroomMembership entity, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
