using System.Net;
using AutoMapper;
using ClassroomService.Application.Abstractions;
using ClassroomService.Application.Common.Mappings;
using ClassroomService.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace ClassroomService.UnitTests;

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
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception), exception));

    public int WarningCount => Entries.Count(e => e.Level == LogLevel.Warning);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

/// <summary>Records notify calls; optionally throws to simulate KnowledgeService down.</summary>
public sealed class RecordingKnowledgeClient : IKnowledgeInternalClient
{
    private readonly bool _throwOnCall;
    public int UploadCalls { get; private set; }
    public int DeleteCalls { get; private set; }
    public Guid LastFileId { get; private set; }

    public RecordingKnowledgeClient(bool throwOnCall = false) => _throwOnCall = throwOnCall;

    public Task NotifyFileUploadedAsync(Guid fileId, Guid classroomId, string s3Key, string fileName, string contentType, CancellationToken ct = default)
    {
        UploadCalls++;
        LastFileId = fileId;
        if (_throwOnCall) throw new HttpRequestException("KnowledgeService is unreachable");
        return Task.CompletedTask;
    }

    public Task NotifyFileDeletedAsync(Guid fileId, CancellationToken ct = default)
    {
        DeleteCalls++;
        LastFileId = fileId;
        if (_throwOnCall) throw new HttpRequestException("KnowledgeService is unreachable");
        return Task.CompletedTask;
    }
}

/// <summary>In-memory IRepository for a single entity type (only what the tests use).</summary>
public sealed class FakeFileRepository : IRepository<ClassroomFile>
{
    private readonly Dictionary<Guid, ClassroomFile> _store = new();
    public int SaveChangesCount { get; private set; }

    public void Seed(ClassroomFile file) => _store[file.Id] = file;

    public Task<ClassroomFile?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(_store.GetValueOrDefault(id));

    public Task AddAsync(ClassroomFile entity, CancellationToken ct = default)
    {
        _store[entity.Id] = entity;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        _store.Remove(id);
        return Task.CompletedTask;
    }

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCount++;
        return Task.FromResult(1);
    }

    public Task UpdateAsync(ClassroomFile entity, CancellationToken ct = default) => Task.CompletedTask;

    public Task<(IEnumerable<ClassroomFile> Items, int TotalCount)> GetPagedAsync(int page, int pageSize, CancellationToken ct = default)
        => Task.FromResult<(IEnumerable<ClassroomFile>, int)>((_store.Values, _store.Count));
}

/// <summary>In-memory IClassroomRepository (only GetByIdAsync is exercised).</summary>
public sealed class FakeClassroomRepository : IClassroomRepository
{
    private readonly Dictionary<Guid, Classroom> _store = new();

    public void Seed(Classroom classroom) => _store[classroom.Id] = classroom;

    public Task<Classroom?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(_store.GetValueOrDefault(id));

    public Task<Classroom?> GetWithDetailsAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(_store.GetValueOrDefault(id));

    public Task<List<Classroom>> GetByTeacherIdAsync(Guid teacherId, CancellationToken ct = default)
        => Task.FromResult(_store.Values.Where(c => c.TeacherId == teacherId).ToList());

    public Task<List<Classroom>> GetEnrolledClassroomsAsync(Guid studentId, CancellationToken ct = default)
        => Task.FromResult(new List<Classroom>());

    public Task AddAsync(Classroom entity, CancellationToken ct = default) { _store[entity.Id] = entity; return Task.CompletedTask; }
    public Task DeleteAsync(Guid id, CancellationToken ct = default) { _store.Remove(id); return Task.CompletedTask; }
    public Task UpdateAsync(Classroom entity, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> SaveChangesAsync(CancellationToken ct = default) => Task.FromResult(1);
    public Task<(IEnumerable<Classroom> Items, int TotalCount)> GetPagedAsync(int page, int pageSize, CancellationToken ct = default)
        => Task.FromResult<(IEnumerable<Classroom>, int)>((_store.Values, _store.Count));
}

/// <summary>No-op file storage that returns a deterministic key / accepts deletes.</summary>
public sealed class FakeFileStorageService : IFileStorageService
{
    public Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType, CancellationToken ct = default)
        => Task.FromResult(fileName);

    public Task DeleteFileAsync(string s3Key, CancellationToken ct = default) => Task.CompletedTask;
}

public static class TestMapper
{
    /// <summary>Real AutoMapper built from the production profile (no mocking).</summary>
    public static IMapper Create()
        => new MapperConfiguration(cfg => cfg.AddProfile<ClassroomProfile>()).CreateMapper();
}
