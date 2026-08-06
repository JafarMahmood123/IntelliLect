using StreamingService.Application.Abstractions;
using StreamingService.Domain.Entities;
using StreamingService.Domain.Enums;
using StreamingService.Infrastructure.Services;
using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Logging;

namespace StreamingService.UnitTests;

/// <summary>Captures each request (method/URI/secret header/body) and returns a
/// caller-supplied response, so client tests need no mocking framework.</summary>
public sealed class CapturingHttpMessageHandler : HttpMessageHandler
{
    public sealed record Captured(HttpMethod Method, Uri? Uri, string? SecretHeader, string? Body);

    private readonly Func<HttpResponseMessage> _responder;
    public List<Captured> Requests { get; } = new();

    public CapturingHttpMessageHandler(Func<HttpResponseMessage> responder) => _responder = responder;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        string? secret = request.Headers.TryGetValues("X-Internal-Secret", out var values)
            ? values.FirstOrDefault()
            : null;
        Requests.Add(new Captured(request.Method, request.RequestUri, secret, body));
        return _responder();
    }
}

/// <summary>Records log entries so tests can assert a warning was written.</summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception), exception));

    public int WarningCount => Entries.Count(e => e.Level == LogLevel.Warning);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

/// <summary>Records notify calls; optionally throws to simulate the assistant being down.</summary>
public sealed class RecordingLiveAssistantClient : ILiveAssistantInternalClient
{
    private readonly bool _throwOnCall;
    public int StartCalls { get; private set; }
    public int EndCalls { get; private set; }
    public Guid LastSessionId { get; private set; }
    public string? LastRoomName { get; private set; }
    public string? LastTeacherIdentity { get; private set; }

    public RecordingLiveAssistantClient(bool throwOnCall = false) => _throwOnCall = throwOnCall;

    public Task NotifySessionStartedAsync(
        Guid sessionId, Guid classroomId, string roomName, string teacherIdentity, CancellationToken ct = default)
    {
        StartCalls++;
        LastSessionId = sessionId;
        LastRoomName = roomName;
        LastTeacherIdentity = teacherIdentity;
        if (_throwOnCall) throw new HttpRequestException("LiveAssistant is unreachable");
        return Task.CompletedTask;
    }

    public Task NotifySessionEndedAsync(Guid sessionId, CancellationToken ct = default)
    {
        EndCalls++;
        LastSessionId = sessionId;
        if (_throwOnCall) throw new HttpRequestException("LiveAssistant is unreachable");
        return Task.CompletedTask;
    }
}

/// <summary>Records recording start/stop calls; optionally throws to simulate egress being
/// down. Returns a caller-supplied egress id (null models recording disabled).</summary>
public sealed class FakeRecordingEgressService : IRecordingEgressService
{
    private readonly bool _throwOnCall;
    private readonly string? _egressId;
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public string? LastRoomName { get; private set; }
    public string? LastStoppedEgressId { get; private set; }

    public FakeRecordingEgressService(string? egressId = "EG_test123", bool throwOnCall = false)
    {
        _egressId = egressId;
        _throwOnCall = throwOnCall;
    }

    public Task<string?> StartRoomRecordingAsync(string roomName, CancellationToken ct = default)
    {
        StartCalls++;
        LastRoomName = roomName;
        if (_throwOnCall) throw new InvalidOperationException("egress unreachable");
        return Task.FromResult(_egressId);
    }

    public Task StopRecordingAsync(string egressId, CancellationToken ct = default)
    {
        StopCalls++;
        LastStoppedEgressId = egressId;
        if (_throwOnCall) throw new InvalidOperationException("egress unreachable");
        return Task.CompletedTask;
    }

    /// <summary>Settles immediately unless <see cref="FinalizationSettles"/> says otherwise.</summary>
    public int FinalizeCalls { get; private set; }
    public bool FinalizationSettles { get; init; } = true;
    public IReadOnlySet<string> ActiveEgressIds { get; init; } = new HashSet<string>();

    public Task<bool> WaitForFinalizationAsync(string egressId, CancellationToken ct = default)
    {
        FinalizeCalls++;
        return Task.FromResult(FinalizationSettles);
    }

    /// <summary>
    /// Separate from <c>throwOnCall</c> so a test can make LISTING fail while start/stop still
    /// work — otherwise "the cycle aborted early" is indistinguishable from "start failed".
    /// </summary>
    public bool ThrowOnGetActive { get; init; }

    public Task<IReadOnlySet<string>> GetActiveEgressIdsAsync(CancellationToken ct = default)
    {
        if (ThrowOnGetActive) throw new InvalidOperationException("egress unreachable");
        return Task.FromResult(ActiveEgressIds);
    }
}

/// <summary>Records room closures; optionally throws to simulate an unreachable media server.</summary>
public sealed class FakeRoomLifecycleService : IRoomLifecycleService
{
    private readonly bool _throwOnCall;
    public int CloseCalls { get; private set; }
    public string? LastClosedRoom { get; private set; }

    public FakeRoomLifecycleService(bool throwOnCall = false) => _throwOnCall = throwOnCall;

    public Task CloseRoomAsync(string roomName, CancellationToken ct = default)
    {
        CloseCalls++;
        LastClosedRoom = roomName;
        if (_throwOnCall) throw new InvalidOperationException("media server unreachable");
        return Task.CompletedTask;
    }

    // Recorded rather than ignored so a test can assert the live policy was pushed to
    // already-connected students, not just baked into the next join token.
    public List<(Guid SessionId, bool Audio, bool Video)> PolicyApplications { get; } = new();

    public Task ApplyStudentPublishPolicyAsync(
        Guid sessionId,
        bool canPublishAudio,
        bool canPublishVideo,
        CancellationToken ct = default)
    {
        PolicyApplications.Add((sessionId, canPublishAudio, canPublishVideo));
        if (_throwOnCall) throw new InvalidOperationException("media server unreachable");
        return Task.CompletedTask;
    }
}

/// <summary>Records the hub broadcasts so tests can assert clients were told the session ended.</summary>
public sealed class RecordingStreamHubContext : IStreamHubContext
{
    private readonly bool _throwOnStatusChange;
    public List<(Guid SessionId, string Status)> StatusChanges { get; } = new();

    public RecordingStreamHubContext(bool throwOnStatusChange = false)
        => _throwOnStatusChange = throwOnStatusChange;

    public Task NotifyStreamStatusChangedAsync(Guid sessionId, string status)
    {
        StatusChanges.Add((sessionId, status));
        if (_throwOnStatusChange) throw new InvalidOperationException("hub unavailable");
        return Task.CompletedTask;
    }

    /// <summary>Recorded so a test can assert students were told their permissions changed.</summary>
    public List<(Guid SessionId, bool Audio, bool Video)> PolicyChanges { get; } = new();

    public Task NotifyPublishPolicyChangedAsync(Guid sessionId, bool canPublishAudio, bool canPublishVideo)
    {
        PolicyChanges.Add((sessionId, canPublishAudio, canPublishVideo));
        return Task.CompletedTask;
    }

    /// <summary>Recorded so a test can assert everyone in the room was told about recording.</summary>
    public List<(Guid SessionId, string State)> RecordingStateChanges { get; } = new();

    public Task NotifyRecordingStateChangedAsync(Guid sessionId, string state)
    {
        RecordingStateChanges.Add((sessionId, state));
        return Task.CompletedTask;
    }

    /// <summary>Recorded so a test can assert the room was told a quiz opened, closed or was cancelled.</summary>
    public List<(Guid SessionId, Guid QuizId, string State)> QuizChanges { get; } = new();

    public Task NotifyQuizChangedAsync(Guid sessionId, Guid quizId, string state)
    {
        QuizChanges.Add((sessionId, quizId, state));
        return Task.CompletedTask;
    }

    public Task NotifyHandRaisedAsync(Guid sessionId, Guid userId, bool isRaised) => Task.CompletedTask;
    public Task NotifyParticipantCountAsync(Guid sessionId, int count) => Task.CompletedTask;
    public Task BroadcastChatMessageAsync(Guid sessionId, Guid userId, string userName, string message) => Task.CompletedTask;
    public Task BroadcastReactionAsync(Guid sessionId, Guid userId, string emoji) => Task.CompletedTask;
}

/// <summary>Captures the requests handed to the LiveKit egress client and returns a canned
/// <see cref="EgressInfo"/>, so the recording service can be tested with no live server.</summary>
public sealed class FakeLiveKitEgressClient : ILiveKitEgressClient
{
    private readonly bool _throwOnCall;
    public string EgressIdToReturn { get; init; } = "EG_generated";
    public int StartCalls { get; private set; }
    public RoomCompositeEgressRequest? LastStartRequest { get; private set; }
    public StopEgressRequest? LastStopRequest { get; private set; }

    public FakeLiveKitEgressClient(bool throwOnCall = false) => _throwOnCall = throwOnCall;

    public Task<EgressInfo> StartRoomCompositeEgressAsync(RoomCompositeEgressRequest request)
    {
        StartCalls++;
        LastStartRequest = request;
        if (_throwOnCall) throw new InvalidOperationException("egress unreachable");
        return Task.FromResult(new EgressInfo { EgressId = EgressIdToReturn, RoomName = request.RoomName });
    }

    public Task<EgressInfo> StopEgressAsync(StopEgressRequest request)
    {
        LastStopRequest = request;
        if (_throwOnCall) throw new InvalidOperationException("egress unreachable");
        return Task.FromResult(new EgressInfo { EgressId = request.EgressId });
    }

    /// <summary>
    /// Statuses handed back by successive ListEgress calls, so a test can walk an egress from
    /// Active through Ending to Complete the way a real finalize does. The last entry repeats;
    /// an empty list models an egress LiveKit no longer tracks.
    /// </summary>
    public IReadOnlyList<EgressStatus> StatusSequence { get; init; } = [EgressStatus.EgressComplete];
    public int ListCalls { get; private set; }
    public ListEgressRequest? LastListRequest { get; private set; }

    /// <summary>
    /// An explicit multi-egress listing, for the "which of these should I stop?" question, which
    /// <see cref="StatusSequence"/> cannot express — it models ONE egress being walked through its
    /// lifecycle. Takes precedence when non-empty.
    /// </summary>
    public List<(string EgressId, EgressStatus Status)> ActiveItems { get; } = new();

    public Task<ListEgressResponse> ListEgressAsync(ListEgressRequest request)
    {
        LastListRequest = request;
        if (_throwOnCall) throw new InvalidOperationException("egress unreachable");

        if (ActiveItems.Count > 0)
        {
            ListCalls++;
            var listing = new ListEgressResponse();
            foreach (var (egressId, status) in ActiveItems)
            {
                listing.Items.Add(new EgressInfo { EgressId = egressId, Status = status });
            }
            return Task.FromResult(listing);
        }

        var index = Math.Min(ListCalls, StatusSequence.Count - 1);
        ListCalls++;

        var response = new ListEgressResponse();
        if (StatusSequence.Count > 0)
        {
            response.Items.Add(new EgressInfo
            {
                EgressId = string.IsNullOrEmpty(request.EgressId) ? EgressIdToReturn : request.EgressId,
                Status = StatusSequence[index],
            });
        }
        return Task.FromResult(response);
    }
}

/// <summary>Captures published messages. Only the typed Publish&lt;T&gt;(message, ct) overload the
/// webhook handler uses is implemented; the rest throw so accidental use is obvious.</summary>
public sealed class FakePublishEndpoint : MassTransit.IPublishEndpoint
{
    public List<object> Published { get; } = new();

    public Task Publish<T>(T message, CancellationToken cancellationToken = default) where T : class
    {
        Published.Add(message);
        return Task.CompletedTask;
    }

    public T? LastOf<T>() where T : class => Published.OfType<T>().LastOrDefault();

    // --- Unused surface -----------------------------------------------------------------
    public Task Publish<T>(T message, MassTransit.IPipe<MassTransit.PublishContext<T>> publishPipe, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();
    public Task Publish<T>(T message, MassTransit.IPipe<MassTransit.PublishContext> publishPipe, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();
    public Task Publish(object message, CancellationToken cancellationToken = default) { Published.Add(message); return Task.CompletedTask; }
    public Task Publish(object message, MassTransit.IPipe<MassTransit.PublishContext> publishPipe, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task Publish(object message, Type messageType, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task Publish(object message, Type messageType, MassTransit.IPipe<MassTransit.PublishContext> publishPipe, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task Publish<T>(object values, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();
    public Task Publish<T>(object values, MassTransit.IPipe<MassTransit.PublishContext<T>> publishPipe, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();
    public Task Publish<T>(object values, MassTransit.IPipe<MassTransit.PublishContext> publishPipe, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();
    public MassTransit.ConnectHandle ConnectPublishObserver(MassTransit.IPublishObserver observer) => throw new NotSupportedException();
}

/// <summary>Returns a canned WebhookEvent (or throws) so the handler can be tested without signing.</summary>
public sealed class FakeLiveKitWebhookVerifier : StreamingService.Infrastructure.Services.ILiveKitWebhookVerifier
{
    private readonly Livekit.Server.Sdk.Dotnet.WebhookEvent? _event;
    private readonly bool _throwInvalid;
    public string? LastBody { get; private set; }
    public string? LastAuthHeader { get; private set; }

    public FakeLiveKitWebhookVerifier(Livekit.Server.Sdk.Dotnet.WebhookEvent? webhookEvent, bool throwInvalid = false)
    {
        _event = webhookEvent;
        _throwInvalid = throwInvalid;
    }

    public Livekit.Server.Sdk.Dotnet.WebhookEvent Verify(string body, string authHeader)
    {
        LastBody = body;
        LastAuthHeader = authHeader;
        if (_throwInvalid) throw new WebhookVerificationException("invalid signature");
        return _event!;
    }
}

/// <summary>Records webhook handler invocations; optionally throws a verification failure.</summary>
public sealed class FakeRecordingWebhookHandler : IRecordingWebhookHandler
{
    private readonly bool _throwInvalid;
    public int Calls { get; private set; }
    public string? LastBody { get; private set; }
    public string? LastAuthHeader { get; private set; }

    public FakeRecordingWebhookHandler(bool throwInvalid = false) => _throwInvalid = throwInvalid;

    public Task HandleAsync(string body, string authHeader, CancellationToken ct = default)
    {
        Calls++;
        LastBody = body;
        LastAuthHeader = authHeader;
        if (_throwInvalid) throw new WebhookVerificationException("invalid signature");
        return Task.CompletedTask;
    }
}

/// <summary>Records recording metric calls so tests can assert instrumentation moved.</summary>
public sealed class FakeRecordingMetrics : IRecordingMetrics
{
    public int StartedCount { get; private set; }

    public void RecordingStarted() => StartedCount++;
}

/// <summary>Minimal in-memory IStreamRepository for controller tests.</summary>
public sealed class FakeStreamRepository : IStreamRepository
{
    private readonly List<LiveStream> _streams = new();

    /// <summary>
    /// Guards every touch of the list above.
    ///
    /// The in-memory transport delivers concurrently, so a test that publishes two messages has
    /// two consumer invocations calling AddAsync at the same moment — and List&lt;T&gt; loses a
    /// write under that. It showed up as "Two_different_sessions_each_get_their_own_stream" failing
    /// roughly one run in six, which reads exactly like a product bug in the idempotency check and
    /// is not one. A flake that accuses the code under test is worse than no test.
    /// </summary>
    private readonly object _gate = new();

    private int _saveCalls;
    public int SaveCalls { get { lock (_gate) return _saveCalls; } }

    public FakeStreamRepository(params LiveStream[] seed) => _streams.AddRange(seed);

    public LiveStream? Find(Guid sessionId)
    {
        lock (_gate) return _streams.FirstOrDefault(s => s.SessionId == sessionId);
    }

    /// <summary>How many rows exist for a session — one is the only correct answer.</summary>
    public int Count(Guid sessionId)
    {
        lock (_gate) return _streams.Count(s => s.SessionId == sessionId);
    }

    /// <summary>Makes the next write fail, to reach a consumer's error path without a database.</summary>
    public bool ThrowOnAdd { get; set; }

    public Task<bool> ExistsAsync(Guid sessionId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_streams.Any(s => s.SessionId == sessionId));
    }

    public Task<LiveStream?> GetBySessionIdAsync(Guid sessionId, bool includeParticipants = false, CancellationToken ct = default)
        => Task.FromResult<LiveStream?>(Find(sessionId));

    public Task<LiveStream?> GetByEgressIdAsync(string egressId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<LiveStream?>(_streams.FirstOrDefault(s => s.EgressId == egressId));
    }

    public Task<List<LiveStream>> GetLiveStreamsAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_streams.Where(s => s.Status == StreamStatus.Live).ToList());
    }

    public int ClaimAttempts { get; private set; }

    /// <summary>
    /// Forces the next claim to lose. A sequential test cannot otherwise reach that branch: once
    /// the first caller writes an egress id, later callers are turned away by the cheap
    /// read-then-write guard and never attempt a claim at all. This models the interleaving that
    /// the guard cannot survive — two callers both reading NULL before either writes.
    /// </summary>
    public bool FailNextClaim { get; set; }

    /// <summary>
    /// Models the real conditional UPDATE: the write only lands when no egress id is set, so a
    /// second caller loses exactly as it would against the database.
    /// </summary>
    public Task<bool> TryClaimEgressSlotAsync(
        Guid sessionId, string placeholderEgressId, CancellationToken ct = default)
    {
        ClaimAttempts++;

        if (FailNextClaim)
        {
            FailNextClaim = false;
            return Task.FromResult(false);
        }

        var stream = Find(sessionId);
        if (stream is null || stream.EgressId is not null) return Task.FromResult(false);

        stream.EgressId = placeholderEgressId;
        return Task.FromResult(true);
    }

    public Task SetEgressIdAsync(Guid streamId, string? egressId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var stream = _streams.FirstOrDefault(s => s.Id == streamId);
            if (stream is not null) stream.EgressId = egressId;
        }
        return Task.CompletedTask;
    }

    public Task<List<LiveStream>> GetLiveStreamsNeedingRecordingAsync(
        string placeholderPrefix, DateTime claimedBeforeUtc, CancellationToken ct = default)
    {
        // Mirrors the real query, RecordingState filter included: without it this fake would keep
        // handing the reconciler sessions the teacher never asked to record.
        var candidates = _streams.Where(s =>
            s.Status == StreamStatus.Live
            && s.RecordingState == RecordingState.Recording
            && s.StartedAtUtc is not null
            && s.StartedAtUtc <= claimedBeforeUtc
            && (s.EgressId is null || s.EgressId.StartsWith(placeholderPrefix, StringComparison.Ordinal)));

        return Task.FromResult(candidates
            .Where(s => s.EgressId is null
                        || !long.TryParse(s.EgressId[placeholderPrefix.Length..], out var ticks)
                        || new DateTime(ticks, DateTimeKind.Utc) <= claimedBeforeUtc)
            .ToList());
    }

    public Task AddAsync(LiveStream entity, CancellationToken ct = default)
    {
        if (ThrowOnAdd) throw new InvalidOperationException("database unavailable");
        lock (_gate) _streams.Add(entity);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(LiveStream entity, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate) _streams.RemoveAll(s => s.Id == id);
        return Task.CompletedTask;
    }

    public Task<LiveStream?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<LiveStream?>(_streams.FirstOrDefault(s => s.Id == id));

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        lock (_gate) _saveCalls++;
        return Task.FromResult(1);
    }
}
