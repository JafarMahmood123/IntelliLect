using IntelliLect.Contracts.Messages;
using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Session;
using ClassroomService.Application.Exceptions;
using ClassroomService.Application.Services;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassroomService.UnitTests;

/// <summary>
/// The teacher-initiated end of a live session: only the classroom's own teacher may close it,
/// and once claimed the teardown (evict participants via the stream end, then trigger the summary)
/// runs best-effort so one failing dependency never leaves the session stuck Live.
/// </summary>
public class TeacherEndSessionTests
{
    [Fact]
    public async Task EndSession_ByOwningTeacher_EndsSessionAndRunsTeardown()
    {
        var (teacherId, classroom, session) = LiveClassroomSession();
        var sessions = new FakeSessionRepository(session);
        var streaming = new RecordingStreamingClient(endResult: true);
        var knowledge = new RecordingKnowledgeClient { SummaryTriggerResult = true };
        var eventBus = new RecordingEventBus();
        var summaries = new FakeSummaryRepository();
        var sut = CreateSut(sessions, classroom, streaming, knowledge, eventBus, summaries);

        var outcome = await sut.EndSessionAsync(classroom.Id, session.Id, teacherId);

        Assert.Equal(SessionStatus.Ended, session.Status);
        Assert.NotNull(session.EndedAtUtc);
        Assert.Equal("Ended", outcome.Status);
        Assert.False(outcome.AlreadyEnded);

        // The stream end is what disconnects the students.
        Assert.Equal(session.Id, streaming.LastEndedSessionId);
        Assert.True(outcome.StreamEnded);

        // The summary is REQUESTED ON THE BUS, committed with the session-end claim. The old
        // HTTP POST could fail silently, leaving a summary owed to nobody.
        Assert.Equal(0, knowledge.SummaryCalls);
        var requested = Assert.Single(eventBus.PublishedOf<SessionSummaryRequestedMessage>());
        Assert.Equal(session.Id, requested.SessionId);
        Assert.Equal(SummaryRequestReasons.SessionEnded, requested.Reason);
        Assert.True(outcome.SummaryTriggered);

        // The Generating row now really exists. Before this, no row was ever written in that
        // state, so "never requested" and "in flight" were indistinguishable in the UI.
        var summary = Assert.Single(summaries.Store);
        Assert.Equal(SummaryStatus.Generating, summary.Status);
        Assert.Equal(session.Id, summary.SessionId);
    }

    [Fact]
    public async Task EndSession_ByAnotherTeacher_IsForbiddenAndChangesNothing()
    {
        var (_, classroom, session) = LiveClassroomSession();
        var sessions = new FakeSessionRepository(session);
        var streaming = new RecordingStreamingClient(endResult: true);
        var sut = CreateSut(sessions, classroom, streaming, new RecordingKnowledgeClient());

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => sut.EndSessionAsync(classroom.Id, session.Id, Guid.NewGuid()));

        Assert.Equal(SessionStatus.Live, session.Status);
        Assert.Equal(0, sessions.SaveCalls);
        Assert.Null(streaming.LastEndedSessionId);
    }

    [Fact]
    public async Task EndSession_UnderTheWrongClassroom_IsNotFound()
    {
        // Addressing someone else's session through your own classroom must not reveal it exists.
        var (teacherId, classroom, session) = LiveClassroomSession();
        var sessions = new FakeSessionRepository(session);
        var sut = CreateSut(sessions, classroom, new RecordingStreamingClient(true), new RecordingKnowledgeClient());

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => sut.EndSessionAsync(Guid.NewGuid(), session.Id, teacherId));

        Assert.Equal(SessionStatus.Live, session.Status);
    }

    [Fact]
    public async Task EndSession_OnAlreadyEndedSession_IsReportedNotRepeated()
    {
        // A double click, or a retry after a dropped response, must not run the teardown twice.
        var (teacherId, classroom, session) = LiveClassroomSession();
        session.Status = SessionStatus.Ended;
        session.EndedAtUtc = DateTime.UtcNow.AddMinutes(-5);
        var sessions = new FakeSessionRepository(session);
        var streaming = new RecordingStreamingClient(endResult: true);
        var knowledge = new RecordingKnowledgeClient();
        var sut = CreateSut(sessions, classroom, streaming, knowledge);

        var outcome = await sut.EndSessionAsync(classroom.Id, session.Id, teacherId);

        Assert.True(outcome.AlreadyEnded);
        Assert.Equal(0, sessions.SaveCalls);
        Assert.Null(streaming.LastEndedSessionId);
        Assert.Equal(0, knowledge.SummaryCalls);
    }

    [Fact]
    public async Task EndSession_WhenAnotherCallerWinsTheRace_DoesNotRunTeardownTwice()
    {
        // The teacher and the stalled sweep firing together: the loser of the atomic claim must
        // stop, or the room would be closed and the summary triggered twice.
        var (teacherId, classroom, session) = LiveClassroomSession();
        var sessions = new FakeSessionRepository(session) { FailEndClaim = true };
        var streaming = new RecordingStreamingClient(endResult: true);
        var knowledge = new RecordingKnowledgeClient();
        var sut = CreateSut(sessions, classroom, streaming, knowledge);

        var outcome = await sut.EndSessionAsync(classroom.Id, session.Id, teacherId);

        Assert.Equal(0, sessions.SaveCalls);
        Assert.Null(streaming.LastEndedSessionId);
        Assert.Equal(0, knowledge.SummaryCalls);
        Assert.Null(outcome.EndedAtUtc);
    }

    [Fact]
    public async Task EndSession_WhenStreamTeardownFails_SessionIsStillEnded()
    {
        // The session must never be left Live because StreamingService was unreachable — the
        // teacher would have no way to close it and no summary would ever be produced.
        var (teacherId, classroom, session) = LiveClassroomSession();
        var sessions = new FakeSessionRepository(session);
        var streaming = new RecordingStreamingClient(endResult: false);
        var knowledge = new RecordingKnowledgeClient { SummaryTriggerResult = true };
        var eventBus = new RecordingEventBus();
        var summaries = new FakeSummaryRepository();
        var sut = CreateSut(sessions, classroom, streaming, knowledge, eventBus, summaries);

        var outcome = await sut.EndSessionAsync(classroom.Id, session.Id, teacherId);

        Assert.Equal(SessionStatus.Ended, session.Status);
        Assert.False(outcome.StreamEnded);
        Assert.True(outcome.SummaryTriggered); // the independent step still ran
    }

    private static SessionService CreateSut(
        FakeSessionRepository sessions,
        Classroom classroom,
        RecordingStreamingClient streaming,
        RecordingKnowledgeClient knowledge,
        RecordingEventBus? eventBus = null,
        FakeSummaryRepository? summaries = null)
        => new(
            sessions,
            new SingleClassroomRepository(classroom),
            streaming,
            SessionTerminationTestFactory.Create(
                sessions, streaming, knowledge, summaries: summaries, eventBus: eventBus),
            new NoOpUnitOfWork());

    private static (Guid TeacherId, Classroom Classroom, Session Session) LiveClassroomSession()
    {
        var teacherId = Guid.NewGuid();
        var classroom = new Classroom { Id = Guid.NewGuid(), Name = "Physics", TeacherId = teacherId };
        var session = new Session
        {
            Id = Guid.NewGuid(),
            ClassroomId = classroom.Id,
            Title = "Lecture 4",
            Status = SessionStatus.Live,
            ScheduledAtUtc = DateTime.UtcNow.AddHours(-1),
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-40),
        };
        return (teacherId, classroom, session);
    }
}

/// <summary>
/// The hourly safety net for sessions nobody ever closed. A session live past the threshold is
/// torn down exactly like a teacher-ended one; one that cannot be closed is left Live so the next
/// pass retries it.
/// </summary>
public class StalledSessionSweeperTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Sweep_EndsSessionsLivePastTheThreshold()
    {
        var stalled = LiveSession(startedHoursAgo: 5);
        var sessions = new FakeSessionRepository(stalled);
        var streaming = new RecordingStreamingClient(endResult: true);
        var knowledge = new RecordingKnowledgeClient { SummaryTriggerResult = true };
        var eventBus = new RecordingEventBus();
        var sut = CreateSut(sessions, streaming, knowledge, eventBus);

        var closed = await sut.SweepAsync();

        Assert.Equal(1, closed);
        Assert.Equal(SessionStatus.Ended, stalled.Status);
        Assert.Equal(Now, stalled.EndedAtUtc);
        Assert.Equal(stalled.Id, streaming.LastEndedSessionId); // students disconnected
        // The sweeper reaches the same termination path, so an abandoned session still gets its
        // summary requested — now on the bus rather than over HTTP.
        var requested = Assert.Single(eventBus.PublishedOf<SessionSummaryRequestedMessage>());
        Assert.Equal(stalled.Id, requested.SessionId);
    }

    [Fact]
    public async Task Sweep_LeavesSessionsInsideTheThresholdAlone()
    {
        // Exactly at the boundary counts as stalled; anything newer is a normal long lecture.
        var justUnder = LiveSession(startedHoursAgo: 3);
        var exactlyAt = LiveSession(startedHoursAgo: 4);
        var sessions = new FakeSessionRepository(justUnder, exactlyAt);
        var sut = CreateSut(sessions, new RecordingStreamingClient(true), new RecordingKnowledgeClient());

        var closed = await sut.SweepAsync();

        Assert.Equal(1, closed);
        Assert.Equal(SessionStatus.Live, justUnder.Status);
        Assert.Equal(SessionStatus.Ended, exactlyAt.Status);
    }

    [Fact]
    public async Task Sweep_IgnoresSessionsThatAreNotLive()
    {
        var scheduledLongAgo = LiveSession(startedHoursAgo: 10);
        scheduledLongAgo.Status = SessionStatus.Scheduled;
        var alreadyEnded = LiveSession(startedHoursAgo: 10);
        alreadyEnded.Status = SessionStatus.Ended;
        var sessions = new FakeSessionRepository(scheduledLongAgo, alreadyEnded);
        var streaming = new RecordingStreamingClient(endResult: true);
        var sut = CreateSut(sessions, streaming, new RecordingKnowledgeClient());

        Assert.Equal(0, await sut.SweepAsync());
        Assert.Null(streaming.LastEndedSessionId);
    }

    [Fact]
    public async Task Sweep_UsesCreationTimeWhenAStartTimestampIsMissing()
    {
        // A Live session with no start time would otherwise never be swept and stay live forever.
        var session = LiveSession(startedHoursAgo: 0);
        session.StartedAtUtc = null;
        session.CreatedAtUtc = Now.AddHours(-9);
        var sessions = new FakeSessionRepository(session);
        var sut = CreateSut(sessions, new RecordingStreamingClient(true), new RecordingKnowledgeClient());

        Assert.Equal(1, await sut.SweepAsync());
        Assert.Equal(SessionStatus.Ended, session.Status);
    }

    [Fact]
    public async Task Sweep_ContinuesWithTheRestOfTheBatchWhenOneSessionFails()
    {
        var failing = LiveSession(startedHoursAgo: 8);
        var healthy = LiveSession(startedHoursAgo: 6);
        var sessions = new FakeSessionRepository(failing, healthy);
        var termination = new ThrowingTerminationService(
            SessionTerminationTestFactory.Create(
                sessions, new RecordingStreamingClient(true), new RecordingKnowledgeClient(), new FakeClock { UtcNow = Now }),
            throwFor: failing.Id);
        var sut = new StalledSessionSweeper(
            sessions, termination, new FakeStalledSessionSettings(), new FakeClock { UtcNow = Now },
            NullLogger<StalledSessionSweeper>.Instance);

        var closed = await sut.SweepAsync();

        Assert.Equal(1, closed);
        Assert.Equal(SessionStatus.Live, failing.Status);  // stays live -> retried next pass
        Assert.Equal(SessionStatus.Ended, healthy.Status);
    }

    [Fact]
    public async Task Sweep_HonoursTheBatchLimit()
    {
        var sessions = new FakeSessionRepository(
            LiveSession(startedHoursAgo: 9), LiveSession(startedHoursAgo: 8), LiveSession(startedHoursAgo: 7));
        var sut = new StalledSessionSweeper(
            sessions,
            SessionTerminationTestFactory.Create(
                sessions, new RecordingStreamingClient(true), new RecordingKnowledgeClient(),
                new FakeClock { UtcNow = Now }),
            new FakeStalledSessionSettings { StalledSweepBatchSize = 2 },
            new FakeClock { UtcNow = Now },
            NullLogger<StalledSessionSweeper>.Instance);

        Assert.Equal(2, await sut.SweepAsync());
    }

    private static StalledSessionSweeper CreateSut(
        FakeSessionRepository sessions,
        RecordingStreamingClient streaming,
        RecordingKnowledgeClient knowledge,
        RecordingEventBus? eventBus = null)
    {
        var clock = new FakeClock { UtcNow = Now };
        return new StalledSessionSweeper(
            sessions,
            SessionTerminationTestFactory.Create(sessions, streaming, knowledge, clock, eventBus: eventBus),
            new FakeStalledSessionSettings(),
            clock,
            NullLogger<StalledSessionSweeper>.Instance);
    }

    private static Session LiveSession(int startedHoursAgo) => new()
    {
        Id = Guid.NewGuid(),
        ClassroomId = Guid.NewGuid(),
        Title = "Lecture",
        Status = SessionStatus.Live,
        CreatedAtUtc = Now.AddHours(-startedHoursAgo - 1),
        ScheduledAtUtc = Now.AddHours(-startedHoursAgo),
        StartedAtUtc = Now.AddHours(-startedHoursAgo),
    };
}

/// <summary>Classroom repository double serving a single known classroom.</summary>
public sealed class SingleClassroomRepository : IClassroomRepository
{
    private readonly Classroom _classroom;
    public SingleClassroomRepository(Classroom classroom) => _classroom = classroom;

    public Task<Classroom?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(id == _classroom.Id ? _classroom : null);

    public Task<Classroom?> GetWithDetailsAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<Classroom>> GetByTeacherIdAsync(Guid teacherId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<Classroom>> GetEnrolledClassroomsAsync(Guid studentId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<(List<Application.DTOs.Classroom.AdminClassroomResponse> Items, int TotalCount)> GetAdminPagedAsync(
        int page, int pageSize, string? search, Guid? teacherId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Application.DTOs.Classroom.AdminClassroomResponse?> GetAdminByIdAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> UpdateWithConcurrencyAsync(Guid id, string name, string description, long expectedVersion, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Application.DTOs.Classroom.ClassroomTeacherInfo?> GetTeacherInfoAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> HasLiveSessionAsync(Guid classroomId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> ChangeTeacherWithConcurrencyAsync(Guid id, Guid newTeacherId, long expectedVersion, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<(Guid Id, string Name)>> GetNamesByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IEnumerable<Classroom>> GetAllAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<(IEnumerable<Classroom> Items, int TotalCount)> GetPagedAsync(int page, int pageSize, CancellationToken ct = default) => throw new NotImplementedException();
    public Task AddAsync(Classroom entity, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpdateAsync(Classroom entity, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<int> SaveChangesAsync(CancellationToken ct = default) => Task.FromResult(0);
}

/// <summary>Ending a session needs no transaction of its own — the claim is a single statement.</summary>
public sealed class NoOpUnitOfWork : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct = default) => Task.FromResult(0);
    public Task BeginTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task RollbackAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FakeStalledSessionSettings : IStalledSessionSettings
{
    public int StalledAfterHours { get; init; } = 4;
    public int StalledSweepBatchSize { get; init; } = 50;
}

/// <summary>Wraps the real termination service, failing for one chosen session.</summary>
public sealed class ThrowingTerminationService : ISessionTerminationService
{
    private readonly ISessionTerminationService _inner;
    private readonly Guid _throwFor;

    public ThrowingTerminationService(ISessionTerminationService inner, Guid throwFor)
    {
        _inner = inner;
        _throwFor = throwFor;
    }

    public Task<SessionEndOutcome> EndAsync(
        Guid sessionId, SessionEndTrigger trigger, string reason, CancellationToken ct = default)
        => sessionId == _throwFor
            ? throw new InvalidOperationException("boom")
            : _inner.EndAsync(sessionId, trigger, reason, ct);
}
