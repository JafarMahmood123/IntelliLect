using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;
using UserManagementService.Application.DTOs.Session;
using UserManagementService.Application.SessionMonitoring;
using UserManagementService.Domain.Entities;

namespace UserManagementService.UnitTests.SessionMonitoring;

// Unit tests for SessionMonitorService, mirroring the "مراقبة الجلسات والإنهاء القسري" use-case:
//   GetSessionsAsync     -> step 3 (paged list + teacher enrichment).
//   GetLiveSessionsAsync -> step 4 (real-time overlay) + 4أ (real-time source unavailable).
//   ForceEndAsync        -> steps 5-8 + 5أ (no reason) + 6أ (not found) + 6ب (not active).
public class SessionMonitorServiceTests
{
    // ----- step 3: listing ------------------------------------------------------

    [Fact]
    public async Task GetSessions_EnrichesTeacherNames()
    {
        var teacher = Teacher("Ada", "Byron");
        var client = new FakeSessionClassroomClient(SessionPage(Session("Lecture 1", teacher.Id, "Live")));
        var sut = CreateSut(client, users: new FakeSessionUserRepository(teacher));

        var result = await sut.GetSessionsAsync(new SearchSessionsRequest { Page = 1, PageSize = 20 });

        var item = Assert.Single(result.Items);
        Assert.Equal("Ada Byron", item.TeacherName);
        Assert.Equal(teacher.Email, item.TeacherEmail);
        Assert.Equal("Lecture 1", item.Title);
        Assert.Equal(1, result.TotalCount);
    }

    // ----- step 4: live view ----------------------------------------------------

    [Fact]
    public async Task GetLiveSessions_MergesRealtimeParticipantsRecordingAndAssistant()
    {
        var teacher = Teacher("Alan", "Turing");
        var session = Session("Live lecture", teacher.Id, "Live");
        var client = new FakeSessionClassroomClient(SessionPage(session));
        var streaming = new FakeStreamingClient(new[]
        {
            new LiveStreamSnapshot(session.SessionId, session.ClassroomId, teacher.Id, ParticipantCount: 12, IsRecording: true, StartedAtUtc: DateTime.UtcNow),
        });
        var assistant = new FakeAssistantClient(new[] { session.SessionId });
        var sut = CreateSut(client, streaming, assistant, new FakeSessionUserRepository(teacher));

        var result = await sut.GetLiveSessionsAsync();

        Assert.False(result.RealtimeUnavailable);
        var item = Assert.Single(result.Items);
        Assert.Equal(12, item.ParticipantCount);
        Assert.True(item.IsRecording);
        Assert.True(item.AssistantRunning);
        Assert.Equal("Alan Turing", item.TeacherName);
    }

    [Fact]
    public async Task GetLiveSessions_WhenStreamingUnavailable_StillListsSessionsAndFlagsIt()
    {
        // Alternate path 4أ: the live snapshot could not be fetched.
        var teacher = Teacher("Grace", "Hopper");
        var session = Session("Live lecture", teacher.Id, "Live");
        var client = new FakeSessionClassroomClient(SessionPage(session));
        var streaming = new FakeStreamingClient(throws: new HttpRequestException("streaming down"));
        var assistant = new FakeAssistantClient(Array.Empty<Guid>());
        var sut = CreateSut(client, streaming, assistant, new FakeSessionUserRepository(teacher));

        var result = await sut.GetLiveSessionsAsync();

        Assert.True(result.RealtimeUnavailable);
        var item = Assert.Single(result.Items);
        // Stored data is still shown; the real-time fields are simply unknown.
        Assert.Equal(session.Title, item.Title);
        Assert.Null(item.ParticipantCount);
        Assert.Null(item.IsRecording);
        Assert.Null(item.AssistantRunning);
    }

    [Fact]
    public async Task GetLiveSessions_WhenAssistantUnavailable_FlagsRealtimeUnavailable()
    {
        // Alternate path 4أ via the assistant source.
        var teacher = Teacher("Grace", "Hopper");
        var session = Session("Live lecture", teacher.Id, "Live");
        var client = new FakeSessionClassroomClient(SessionPage(session));
        var streaming = new FakeStreamingClient(Array.Empty<LiveStreamSnapshot>());
        var assistant = new FakeAssistantClient(throws: new HttpRequestException("assistant down"));
        var sut = CreateSut(client, streaming, assistant, new FakeSessionUserRepository(teacher));

        var result = await sut.GetLiveSessionsAsync();

        Assert.True(result.RealtimeUnavailable);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task GetLiveSessions_OnlyRequestsLiveSessions()
    {
        var client = new FakeSessionClassroomClient(SessionPage());
        var sut = CreateSut(client);

        await sut.GetLiveSessionsAsync();

        Assert.Equal("Live", client.LastStatusFilter);
    }

    // ----- steps 5-8: force-end -------------------------------------------------

    [Fact]
    public async Task ForceEnd_WithReason_DelegatesAndReturnsStepResults()
    {
        var sessionId = Guid.NewGuid();
        var client = new FakeSessionClassroomClient(
            SessionPage(),
            forceEndResult: new ForceEndResult(sessionId, "Ended", AlreadyEnded: false, StreamEnded: true, SummaryTriggered: true));
        var sut = CreateSut(client);

        var result = await sut.ForceEndAsync(sessionId, "  Teacher disconnected  ");

        Assert.Equal("Ended", result.Status);
        Assert.False(result.AlreadyEnded);
        Assert.True(result.StreamEnded);
        Assert.True(result.SummaryTriggered);
        Assert.Equal("Teacher disconnected", client.LastReason); // trimmed
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ForceEnd_WithoutReason_ThrowsAndDoesNotCallClient(string reason)
    {
        // Alternate path 5أ.
        var client = new FakeSessionClassroomClient(SessionPage());
        var sut = CreateSut(client);

        await Assert.ThrowsAsync<ArgumentException>(() => sut.ForceEndAsync(Guid.NewGuid(), reason));

        Assert.False(client.ForceEndCalled);
    }

    [Fact]
    public async Task ForceEnd_WhenSessionNotFound_PropagatesNotFound()
    {
        // Alternate path 6أ.
        var client = new FakeSessionClassroomClient(SessionPage(), forceEndThrows: new NotFoundException("Session not found."));
        var sut = CreateSut(client);

        await Assert.ThrowsAsync<NotFoundException>(() => sut.ForceEndAsync(Guid.NewGuid(), "stalled"));
    }

    [Fact]
    public async Task ForceEnd_WhenSessionNotActive_ReportsAlreadyEndedWithoutError()
    {
        // Alternate path 6ب: no action needed; reported, not an error.
        var sessionId = Guid.NewGuid();
        var client = new FakeSessionClassroomClient(
            SessionPage(),
            forceEndResult: new ForceEndResult(sessionId, "Ended", AlreadyEnded: true, StreamEnded: false, SummaryTriggered: false));
        var sut = CreateSut(client);

        var result = await sut.ForceEndAsync(sessionId, "stalled");

        Assert.True(result.AlreadyEnded);
        Assert.Equal("Ended", result.Status);
    }

    [Fact]
    public async Task ForceEnd_WhenAStepFails_StillReportsSessionEnded()
    {
        // Alternate path 7أ: the session reaches Ended even when a downstream step failed.
        var sessionId = Guid.NewGuid();
        var client = new FakeSessionClassroomClient(
            SessionPage(),
            forceEndResult: new ForceEndResult(sessionId, "Ended", AlreadyEnded: false, StreamEnded: false, SummaryTriggered: false));
        var sut = CreateSut(client);

        var result = await sut.ForceEndAsync(sessionId, "stalled");

        Assert.Equal("Ended", result.Status);
        Assert.False(result.StreamEnded);
        Assert.False(result.SummaryTriggered);
    }

    // ----- helpers --------------------------------------------------------------

    private static SessionMonitorService CreateSut(
        FakeSessionClassroomClient client,
        FakeStreamingClient? streaming = null,
        FakeAssistantClient? assistant = null,
        FakeSessionUserRepository? users = null)
        => new(
            client,
            streaming ?? new FakeStreamingClient(Array.Empty<LiveStreamSnapshot>()),
            assistant ?? new FakeAssistantClient(Array.Empty<Guid>()),
            users ?? new FakeSessionUserRepository());

    private static AdminSession Session(string title, Guid teacherId, string status) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Math 101", teacherId, title, status,
            DateTime.UtcNow, DateTime.UtcNow, null, "Processing", "Generating", DateTime.UtcNow);

    private static AdminSessionPage SessionPage(params AdminSession[] items) =>
        new(items, items.Length, 1, 20, 1);

    private static User Teacher(string first, string last)
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
            Role = Role.Create(RoleName.Teacher),
        };
        user.Approve();
        return user;
    }
}

// ----- fakes ------------------------------------------------------------------

internal sealed class FakeSessionClassroomClient : IClassroomInternalClient
{
    private readonly AdminSessionPage _page;
    private readonly ForceEndResult? _forceEndResult;
    private readonly Exception? _forceEndThrows;

    public FakeSessionClassroomClient(
        AdminSessionPage page,
        ForceEndResult? forceEndResult = null,
        Exception? forceEndThrows = null)
    {
        _page = page;
        _forceEndResult = forceEndResult;
        _forceEndThrows = forceEndThrows;
    }

    public string? LastStatusFilter { get; private set; }
    public bool ForceEndCalled { get; private set; }
    public string? LastReason { get; private set; }

    public Task<AdminSessionPage> GetSessionsAsync(int page, int pageSize, string? search, string? status, Guid? classroomId, CancellationToken ct = default)
    {
        LastStatusFilter = status;
        return Task.FromResult(_page);
    }

    public Task<ForceEndResult> ForceEndSessionAsync(Guid sessionId, string reason, CancellationToken ct = default)
    {
        ForceEndCalled = true;
        LastReason = reason;
        if (_forceEndThrows is not null)
        {
            return Task.FromException<ForceEndResult>(_forceEndThrows);
        }
        return Task.FromResult(_forceEndResult ?? new ForceEndResult(sessionId, "Ended", false, true, true));
    }

    public Task<UserClassrooms> GetUserClassroomsAsync(Guid userId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AdminClassroomPage> GetClassroomsAsync(int page, int pageSize, string? search, Guid? teacherId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AdminClassroom?> GetClassroomByIdAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Guid> CreateClassroomAsync(Guid teacherId, string name, string description, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpdateClassroomAsync(Guid id, string name, string description, long version, CancellationToken ct = default) => throw new NotImplementedException();
}

internal sealed class FakeStreamingClient : IStreamingInternalClient
{
    private readonly IReadOnlyList<LiveStreamSnapshot> _snapshots;
    private readonly Exception? _throws;

    public FakeStreamingClient(IReadOnlyList<LiveStreamSnapshot>? snapshots = null, Exception? throws = null)
    {
        _snapshots = snapshots ?? Array.Empty<LiveStreamSnapshot>();
        _throws = throws;
    }

    public Task<IReadOnlyList<LiveStreamSnapshot>> GetLiveStreamsAsync(CancellationToken ct = default)
        => _throws is not null
            ? Task.FromException<IReadOnlyList<LiveStreamSnapshot>>(_throws)
            : Task.FromResult(_snapshots);
}

internal sealed class FakeAssistantClient : ILiveAssistantInternalClient
{
    private readonly IReadOnlyCollection<Guid> _active;
    private readonly Exception? _throws;

    public FakeAssistantClient(IReadOnlyCollection<Guid>? active = null, Exception? throws = null)
    {
        _active = active ?? Array.Empty<Guid>();
        _throws = throws;
    }

    public Task<IReadOnlyCollection<Guid>> GetActiveSessionIdsAsync(CancellationToken ct = default)
        => _throws is not null
            ? Task.FromException<IReadOnlyCollection<Guid>>(_throws)
            : Task.FromResult(_active);
}

internal sealed class FakeSessionUserRepository : IUserRepository
{
    private readonly List<User> _users;
    public FakeSessionUserRepository(params User[] users) => _users = users.ToList();

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
