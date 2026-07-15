using StreamingService.Application.Abstractions;
using StreamingService.Domain.Entities;
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
}

/// <summary>Minimal in-memory IStreamRepository for controller tests.</summary>
public sealed class FakeStreamRepository : IStreamRepository
{
    private readonly List<LiveStream> _streams = new();
    public int SaveCalls { get; private set; }

    public FakeStreamRepository(params LiveStream[] seed) => _streams.AddRange(seed);

    public LiveStream? Find(Guid sessionId) => _streams.FirstOrDefault(s => s.SessionId == sessionId);

    public Task<bool> ExistsAsync(Guid sessionId, CancellationToken ct = default)
        => Task.FromResult(_streams.Any(s => s.SessionId == sessionId));

    public Task<LiveStream?> GetBySessionIdAsync(Guid sessionId, bool includeParticipants = false, CancellationToken ct = default)
        => Task.FromResult<LiveStream?>(Find(sessionId));

    public Task AddAsync(LiveStream entity, CancellationToken ct = default)
    {
        _streams.Add(entity);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(LiveStream entity, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        _streams.RemoveAll(s => s.Id == id);
        return Task.CompletedTask;
    }

    public Task<LiveStream?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<LiveStream?>(_streams.FirstOrDefault(s => s.Id == id));

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        SaveCalls++;
        return Task.FromResult(1);
    }
}
