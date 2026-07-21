using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Classroom;
using ClassroomService.Application.Exceptions;
using ClassroomService.Application.Services;
using ClassroomService.Domain.Entities;

namespace ClassroomService.UnitTests;

// Unit tests for ClassroomManagementService.ChangeTeacherAsync — the ownership-transfer use-case
// ("إسناد معلم الفصل الدراسي أو تغييره"), the part ClassroomService owns:
//   3أ -> classroom missing. 3ب -> a live session is in progress. 4ب -> new teacher already owns
//   it (no-op). Step 5 -> reassign under optimistic concurrency; a stale version -> ConflictException.
public class ChangeTeacherServiceTests
{
    private static readonly Guid ClassroomId = Guid.NewGuid();
    private static readonly Guid CurrentTeacher = Guid.NewGuid();
    private static readonly Guid NewTeacher = Guid.NewGuid();

    [Fact]
    public async Task ChangeTeacher_WhenClassroomMissing_ThrowsKeyNotFound()
    {
        var repo = new FakeRepo { Info = null }; // 3أ
        var sut = new ClassroomManagementService(repo, mapper: null!);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => sut.ChangeTeacherAsync(ClassroomId, NewTeacher, expectedVersion: 1));

        Assert.False(repo.ChangeCalled);
    }

    [Fact]
    public async Task ChangeTeacher_WhenLiveSession_ThrowsConflictAndDoesNotWrite()
    {
        var repo = new FakeRepo
        {
            Info = new ClassroomTeacherInfo(CurrentTeacher, "Physics"),
            Live = true, // 3ب
        };
        var sut = new ClassroomManagementService(repo, mapper: null!);

        await Assert.ThrowsAsync<ConflictException>(
            () => sut.ChangeTeacherAsync(ClassroomId, NewTeacher, expectedVersion: 1));

        Assert.False(repo.ChangeCalled); // ownership must not move while a lecture is live
    }

    [Fact]
    public async Task ChangeTeacher_WhenNewTeacherIsCurrent_ReturnsNoOp()
    {
        var repo = new FakeRepo { Info = new ClassroomTeacherInfo(CurrentTeacher, "Physics") };
        var sut = new ClassroomManagementService(repo, mapper: null!);

        // 4ب: assigning the classroom to the teacher who already owns it changes nothing.
        var result = await sut.ChangeTeacherAsync(ClassroomId, CurrentTeacher, expectedVersion: 1);

        Assert.False(result.Changed);
        Assert.Equal(CurrentTeacher, result.PreviousTeacherId);
        Assert.Equal(CurrentTeacher, result.NewTeacherId);
        Assert.Equal("Physics", result.ClassroomName);
        Assert.False(repo.ChangeCalled); // no write performed
    }

    [Fact]
    public async Task ChangeTeacher_HappyPath_TransfersOwnership()
    {
        var repo = new FakeRepo
        {
            Info = new ClassroomTeacherInfo(CurrentTeacher, "Physics"),
            ChangeReturns = true,
        };
        var sut = new ClassroomManagementService(repo, mapper: null!);

        var result = await sut.ChangeTeacherAsync(ClassroomId, NewTeacher, expectedVersion: 42);

        Assert.True(result.Changed);
        Assert.Equal(CurrentTeacher, result.PreviousTeacherId);
        Assert.Equal(NewTeacher, result.NewTeacherId);
        Assert.Equal("Physics", result.ClassroomName);

        Assert.True(repo.ChangeCalled);
        Assert.Equal(NewTeacher, repo.ChangedToTeacher);
        Assert.Equal(42, repo.ChangedWithVersion);
    }

    [Fact]
    public async Task ChangeTeacher_WhenVersionStale_PropagatesConflict()
    {
        var repo = new FakeRepo
        {
            Info = new ClassroomTeacherInfo(CurrentTeacher, "Physics"),
            ChangeThrows = new ConflictException("stale"),
        };
        var sut = new ClassroomManagementService(repo, mapper: null!);

        await Assert.ThrowsAsync<ConflictException>(
            () => sut.ChangeTeacherAsync(ClassroomId, NewTeacher, expectedVersion: 1));
    }

    [Fact]
    public async Task ChangeTeacher_WhenRowVanishesDuringWrite_ThrowsKeyNotFound()
    {
        var repo = new FakeRepo
        {
            Info = new ClassroomTeacherInfo(CurrentTeacher, "Physics"),
            ChangeReturns = false, // classroom deleted between the read and the write
        };
        var sut = new ClassroomManagementService(repo, mapper: null!);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => sut.ChangeTeacherAsync(ClassroomId, NewTeacher, expectedVersion: 1));
    }

    // --- fake ------------------------------------------------------------------

    private sealed class FakeRepo : IClassroomRepository
    {
        public ClassroomTeacherInfo? Info;
        public bool Live;
        public bool ChangeReturns = true;
        public Exception? ChangeThrows;

        public bool ChangeCalled { get; private set; }
        public Guid ChangedToTeacher { get; private set; }
        public long ChangedWithVersion { get; private set; }

        public Task<ClassroomTeacherInfo?> GetTeacherInfoAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(Info);

        public Task<bool> HasLiveSessionAsync(Guid classroomId, CancellationToken ct = default)
            => Task.FromResult(Live);

        public Task<bool> ChangeTeacherWithConcurrencyAsync(
            Guid id, Guid newTeacherId, long expectedVersion, CancellationToken ct = default)
        {
            ChangeCalled = true;
            ChangedToTeacher = newTeacherId;
            ChangedWithVersion = expectedVersion;
            if (ChangeThrows is not null) return Task.FromException<bool>(ChangeThrows);
            return Task.FromResult(ChangeReturns);
        }

        // --- unused by the change-teacher path -----------------------------------
        public Task<Classroom?> GetWithDetailsAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<Classroom>> GetByTeacherIdAsync(Guid teacherId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<Classroom>> GetEnrolledClassroomsAsync(Guid studentId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(List<AdminClassroomResponse> Items, int TotalCount)> GetAdminPagedAsync(int page, int pageSize, string? search, Guid? teacherId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<AdminClassroomResponse?> GetAdminByIdAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> UpdateWithConcurrencyAsync(Guid id, string name, string description, long expectedVersion, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<(Guid Id, string Name)>> GetNamesByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(IEnumerable<Classroom> Items, int TotalCount)> GetPagedAsync(int page, int pageSize, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Classroom?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AddAsync(Classroom entity, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateAsync(Classroom entity, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> SaveChangesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }
}
