using IntelliLect.Contracts.Messages;
using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Session;
using ClassroomService.Application.Services;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassroomService.UnitTests;

// Unit tests for SessionAdminService.ForceEndAsync — the orchestration behind the super-admin
// "الإنهاء القسري" step 7: the session reaches Ended, then the stream-end path and the summary
// trigger run best-effort.
//   6أ -> session not found. 6ب -> not active (no-op). 7أ -> a step fails, the rest still run.
public class SessionAdminServiceTests
{
    [Fact]
    public async Task ForceEnd_OnLiveSession_EndsSessionThenStreamThenSummary()
    {
        var session = LiveSession();
        var sessions = new FakeSessionRepository(session);
        var streaming = new RecordingStreamingClient(endResult: true);
        var knowledge = new RecordingKnowledgeClient { SummaryTriggerResult = true };
        var eventBus = new RecordingEventBus();
        var sut = CreateSut(sessions, streaming, knowledge, eventBus);

        var result = await sut.ForceEndAsync(session.Id, "Teacher disconnected");

        // Postcondition: the session is Ended and persisted.
        Assert.Equal(SessionStatus.Ended, session.Status);
        Assert.NotNull(session.EndedAtUtc);
        Assert.Equal(1, sessions.SaveCalls);

        // The end path ran, and the summary was REQUESTED ON THE BUS rather than POSTed: the
        // HTTP call could fail silently and leave the summary owed to nobody.
        Assert.Equal(session.Id, streaming.LastEndedSessionId);
        Assert.Equal(0, knowledge.SummaryCalls);
        var requested = Assert.Single(eventBus.PublishedOf<SessionSummaryRequestedMessage>());
        Assert.Equal(session.Id, requested.SessionId);
        Assert.Equal(SummaryRequestReasons.SessionEnded, requested.Reason);

        Assert.Equal("Ended", result.Status);
        Assert.False(result.AlreadyEnded);
        Assert.True(result.StreamEnded);
        Assert.True(result.SummaryTriggered);
    }

    [Fact]
    public async Task ForceEnd_WhenStreamEndFails_StillEndsSessionAndTriggersSummary()
    {
        // Alternate path 7أ: one failing step must not block the others.
        var session = LiveSession();
        var sessions = new FakeSessionRepository(session);
        var streaming = new RecordingStreamingClient(endResult: false); // stream end failed
        var knowledge = new RecordingKnowledgeClient { SummaryTriggerResult = true };
        var eventBus = new RecordingEventBus();
        var sut = CreateSut(sessions, streaming, knowledge, eventBus);

        var result = await sut.ForceEndAsync(session.Id, "stalled");

        Assert.Equal(SessionStatus.Ended, session.Status); // reached Ended regardless
        Assert.False(result.StreamEnded);
        // The request is now committed BEFORE the stream teardown is attempted, so a failing
        // teardown cannot cost us the summary at all.
        Assert.True(result.SummaryTriggered);
        Assert.Single(eventBus.PublishedOf<SessionSummaryRequestedMessage>());
    }

    [Fact]
    public async Task ForceEnd_WhenSummaryIsBeingDeleted_SessionStillEndedButNoRequest()
    {
        // Alternate path 7أ, other direction. The summary trigger can no longer "fail" on its
        // own — it is an outbox write inside the end transaction, so a failure rolls the whole
        // thing back. The one case that still declines to request is a summary mid-deletion:
        // re-requesting would race the file removal and could orphan objects in S3.
        var session = LiveSession();
        var sessions = new FakeSessionRepository(session);
        var summaries = new FakeSummaryRepository();
        summaries.Seed(new SessionSummary
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            ClassroomId = session.ClassroomId,
            Status = SummaryStatus.PendingDeletion,
        });
        var streaming = new RecordingStreamingClient(endResult: true);
        var eventBus = new RecordingEventBus();
        var sut = CreateSut(sessions, streaming, new RecordingKnowledgeClient(), eventBus, summaries);

        var result = await sut.ForceEndAsync(session.Id, "stalled");

        Assert.Equal(SessionStatus.Ended, session.Status);
        Assert.Equal("Ended", result.Status);
        Assert.True(result.StreamEnded);
        Assert.False(result.SummaryTriggered);
        Assert.Empty(eventBus.PublishedOf<SessionSummaryRequestedMessage>());
    }

    [Fact]
    public async Task ForceEnd_WhenSessionMissing_ThrowsNotFound()
    {
        // Alternate path 6أ.
        var sessions = new FakeSessionRepository();
        var streaming = new RecordingStreamingClient(endResult: true);
        var knowledge = new RecordingKnowledgeClient();
        var sut = CreateSut(sessions, streaming, knowledge);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => sut.ForceEndAsync(Guid.NewGuid(), "stalled"));

        Assert.Equal(0, sessions.SaveCalls);
        Assert.Null(streaming.LastEndedSessionId);
        Assert.Equal(0, knowledge.SummaryCalls);
    }

    [Theory]
    [InlineData(SessionStatus.Ended)]
    [InlineData(SessionStatus.Scheduled)]
    public async Task ForceEnd_WhenSessionNotLive_IsNoOp(SessionStatus status)
    {
        // Alternate path 6ب: nothing is changed and no downstream step runs.
        var session = LiveSession();
        session.Status = status;
        var sessions = new FakeSessionRepository(session);
        var streaming = new RecordingStreamingClient(endResult: true);
        var knowledge = new RecordingKnowledgeClient();
        var sut = CreateSut(sessions, streaming, knowledge);

        var result = await sut.ForceEndAsync(session.Id, "stalled");

        Assert.Equal(status, session.Status);
        Assert.Equal(0, sessions.SaveCalls);
        Assert.Null(streaming.LastEndedSessionId);
        Assert.Equal(0, knowledge.SummaryCalls);
        Assert.Equal(status == SessionStatus.Ended, result.AlreadyEnded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ForceEnd_WithoutReason_Throws(string reason)
    {
        // Alternate path 5أ (defence in depth; also validated upstream).
        var session = LiveSession();
        var sessions = new FakeSessionRepository(session);
        var sut = CreateSut(sessions, new RecordingStreamingClient(true), new RecordingKnowledgeClient());

        await Assert.ThrowsAsync<ArgumentException>(() => sut.ForceEndAsync(session.Id, reason));

        Assert.Equal(SessionStatus.Live, session.Status);
        Assert.Equal(0, sessions.SaveCalls);
    }

    // The admin force-end delegates to the shared termination path, so the real one is wired in:
    // these tests cover the whole teardown, not just the admin wrapper.
    private static SessionAdminService CreateSut(
        FakeSessionRepository sessions,
        RecordingStreamingClient streaming,
        RecordingKnowledgeClient knowledge,
        RecordingEventBus? eventBus = null,
        FakeSummaryRepository? summaries = null)
        => new(sessions, SessionTerminationTestFactory.Create(
            sessions, streaming, knowledge,
            summaries: summaries,
            eventBus: eventBus));

    private static Session LiveSession() => new()
    {
        Id = Guid.NewGuid(),
        ClassroomId = Guid.NewGuid(),
        Title = "Lecture",
        Status = SessionStatus.Live,
        ScheduledAtUtc = DateTime.UtcNow.AddHours(-1),
        StartedAtUtc = DateTime.UtcNow.AddMinutes(-50),
    };
}

/// <summary>Builds a real <see cref="SessionTerminationService"/> over test doubles.</summary>
public static class SessionTerminationTestFactory
{
    /// <summary>
    /// Builds the termination service with the collaborators a test cares about.
    /// </summary>
    /// <remarks>
    /// <paramref name="knowledge"/> is accepted but unused: the summary request moved from a
    /// synchronous HTTP call to an outboxed event, so it is kept only so the many existing call
    /// sites still compile. Assert on <paramref name="eventBus"/> instead.
    /// </remarks>
    public static SessionTerminationService Create(
        ISessionRepository sessions,
        IStreamingInternalClient streaming,
        IKnowledgeInternalClient knowledge,
        IClock? clock = null,
        ISummaryRepository? summaries = null,
        IEventBus? eventBus = null,
        IUnitOfWork? unitOfWork = null)
        => new(sessions,
            summaries ?? new FakeSummaryRepository(),
            streaming,
            eventBus ?? new RecordingEventBus(),
            unitOfWork ?? new RecordingUnitOfWork(),
            clock ?? new FakeClock(),
            NullLogger<SessionTerminationService>.Instance);
}

/// <summary>In-memory session store recording saves.</summary>
public sealed class FakeSessionRepository : ISessionRepository
{
    private readonly Dictionary<Guid, Session> _store = new();
    public int SaveCalls { get; private set; }

    /// <summary>
    /// Simulates losing the Live -> Ended race (another caller ended the session first), which the
    /// database arbitrates in production.
    /// </summary>
    public bool FailEndClaim { get; set; }

    public FakeSessionRepository(params Session[] seed)
    {
        foreach (var s in seed) _store[s.Id] = s;
    }

    public Task<Session?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(_store.GetValueOrDefault(id));

    public Task<bool> TryMarkEndedAsync(Guid sessionId, DateTime endedAtUtc, CancellationToken ct = default)
    {
        var session = _store.GetValueOrDefault(sessionId);
        if (FailEndClaim || session is null || session.Status != SessionStatus.Live)
        {
            return Task.FromResult(false);
        }

        session.Status = SessionStatus.Ended;
        session.EndedAtUtc = endedAtUtc;
        SaveCalls++; // the claim IS the persist
        return Task.FromResult(true);
    }

    public Task<List<Guid>> GetStalledLiveSessionIdsAsync(
        DateTime startedBeforeUtc, int limit, CancellationToken ct = default)
        => Task.FromResult(_store.Values
            .Where(s => s.Status == SessionStatus.Live
                        && (s.StartedAtUtc ?? s.CreatedAtUtc) <= startedBeforeUtc)
            .OrderBy(s => s.StartedAtUtc ?? s.CreatedAtUtc)
            .Take(limit)
            .Select(s => s.Id)
            .ToList());

    public Task UpdateAsync(Session session, CancellationToken ct = default) => Task.CompletedTask;

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveCalls++;
        return Task.CompletedTask;
    }

    public Task<IEnumerable<Session>> GetByClassroomIdAsync(Guid classroomId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task AddAsync(Session session, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<(List<AdminSessionResponse> Items, int TotalCount)> GetAdminSessionsPagedAsync(
        int page, int pageSize, string? search, SessionStatus? status, Guid? classroomId, CancellationToken ct = default)
        => throw new NotImplementedException();
}

/// <summary>Streaming client double recording the end call and its configured outcome.</summary>
public sealed class RecordingStreamingClient : IStreamingInternalClient
{
    private readonly bool _endResult;
    public RecordingStreamingClient(bool endResult) => _endResult = endResult;

    public Guid? LastEndedSessionId { get; private set; }

    public Task<bool> EndStreamAsync(Guid sessionId, CancellationToken ct = default)
    {
        LastEndedSessionId = sessionId;
        return Task.FromResult(_endResult);
    }

    public Task<bool> CreateStreamAsync(Guid sessionId, Guid classroomId, Guid teacherId, StudentParticipationMode participationMode, CancellationToken ct = default)
        => throw new NotImplementedException();
}
