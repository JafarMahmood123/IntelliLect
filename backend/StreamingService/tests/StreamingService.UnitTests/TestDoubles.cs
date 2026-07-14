using StreamingService.Application.Abstractions;
using StreamingService.Domain.Entities;
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
