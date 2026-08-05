using System.Net;
using AutoMapper;
using Microsoft.Extensions.Logging.Abstractions;
using ClassroomService.Application.Abstractions;
using ClassroomService.Application.Common.Mappings;
using ClassroomService.Application.Models;
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

/// <summary>Records notify calls; optionally throws to simulate RagService down.</summary>
public sealed class RecordingKnowledgeClient : IRagInternalClient
{
    private readonly bool _throwOnCall;
    public int UploadCalls { get; private set; }
    public int DeleteCalls { get; private set; }
    public int StatusCalls { get; private set; }
    public Guid LastFileId { get; private set; }
    public Guid LastStatusFileId { get; private set; }

    /// <summary>Status returned by <see cref="GetIndexingStatusAsync"/>; null simulates a
    /// RagService 404 (no document row yet).</summary>
    public string? StatusToReturn { get; set; } = "Done";

    public RecordingKnowledgeClient(bool throwOnCall = false) => _throwOnCall = throwOnCall;

    /// <summary>Records summary triggers; returns <see cref="SummaryTriggerResult"/>.</summary>
    public int SummaryCalls { get; private set; }
    public Guid LastSummarySessionId { get; private set; }
    public bool SummaryTriggerResult { get; set; } = true;

    public Task<bool> TriggerSummaryAsync(Guid sessionId, Guid classroomId, CancellationToken ct = default)
    {
        SummaryCalls++;
        LastSummarySessionId = sessionId;
        return Task.FromResult(SummaryTriggerResult);
    }

    public Task NotifyFileUploadedAsync(Guid fileId, Guid classroomId, string s3Key, string fileName, string contentType, long sizeBytes, CancellationToken ct = default)
    {
        UploadCalls++;
        LastFileId = fileId;
        if (_throwOnCall) throw new HttpRequestException("RagService is unreachable");
        return Task.CompletedTask;
    }

    public Task NotifyFileDeletedAsync(Guid fileId, CancellationToken ct = default)
    {
        DeleteCalls++;
        LastFileId = fileId;
        if (_throwOnCall) throw new HttpRequestException("RagService is unreachable");
        return Task.CompletedTask;
    }

    public int DeIndexClassroomCalls { get; private set; }
    public Guid LastDeIndexedClassroomId { get; private set; }

    public Task DeIndexClassroomAsync(Guid classroomId, CancellationToken ct = default)
    {
        DeIndexClassroomCalls++;
        LastDeIndexedClassroomId = classroomId;
        if (_throwOnCall) throw new HttpRequestException("RagService is unreachable");
        return Task.CompletedTask;
    }

    public Task<string?> GetIndexingStatusAsync(Guid fileId, CancellationToken ct = default)
    {
        StatusCalls++;
        LastStatusFileId = fileId;
        if (_throwOnCall) throw new HttpRequestException("RagService is unreachable");
        return Task.FromResult(StatusToReturn);
    }

    public int AnswerCalls { get; private set; }
    public Guid LastAnswerClassroomId { get; private set; }
    public string? LastAnswerQuestion { get; private set; }

    /// <summary>Answer returned by <see cref="GetAnswerAsync"/>. Defaults to a grounded answer
    /// with one source; set to an empty-source result to simulate "no relevant material".</summary>
    public KnowledgeAnswerResult AnswerToReturn { get; set; } = new(
        "The mitochondria is the powerhouse of the cell [1].",
        new List<KnowledgeAnswerSource> { new(1, Guid.NewGuid(), 4, null, "Cell biology") });

    public Task<KnowledgeAnswerResult> GetAnswerAsync(
        Guid classroomId, string question, CancellationToken ct = default)
    {
        AnswerCalls++;
        LastAnswerClassroomId = classroomId;
        LastAnswerQuestion = question;
        if (_throwOnCall) throw new HttpRequestException("RagService is unreachable");
        return Task.FromResult(AnswerToReturn);
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

    public Task<(List<ClassroomService.Application.DTOs.Classroom.AdminClassroomResponse> Items, int TotalCount)> GetAdminPagedAsync(
        int page, int pageSize, string? search, Guid? teacherId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<ClassroomService.Application.DTOs.Classroom.AdminClassroomResponse?> GetAdminByIdAsync(Guid id, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<bool> UpdateWithConcurrencyAsync(Guid id, string name, string description, long expectedVersion, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<List<(Guid Id, string Name)>> GetNamesByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        => Task.FromResult(_store.Values.Where(c => ids.Contains(c.Id)).Select(c => (c.Id, c.Name)).ToList());

    public Task<ClassroomService.Application.DTOs.Classroom.ClassroomTeacherInfo?> GetTeacherInfoAsync(Guid id, CancellationToken ct = default)
    {
        var classroom = _store.GetValueOrDefault(id);
        return Task.FromResult(classroom is null
            ? null
            : new ClassroomService.Application.DTOs.Classroom.ClassroomTeacherInfo(classroom.TeacherId, classroom.Name));
    }

    public Task<bool> HasLiveSessionAsync(Guid classroomId, CancellationToken ct = default) => Task.FromResult(false);

    public Task<bool> ChangeTeacherWithConcurrencyAsync(Guid id, Guid newTeacherId, long expectedVersion, CancellationToken ct = default)
    {
        var classroom = _store.GetValueOrDefault(id);
        if (classroom is null) return Task.FromResult(false);
        classroom.TeacherId = newTeacherId;
        return Task.FromResult(true);
    }
}

/// <summary>No-op file storage that returns a deterministic key / accepts deletes.</summary>
public sealed class FakeFileStorageService : IFileStorageService
{
    /// <summary>Keys written, so a test can assert a REJECTED upload stored nothing.</summary>
    public List<string> UploadedKeys { get; } = new();

    public Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType, CancellationToken ct = default)
    {
        UploadedKeys.Add(fileName);
        return Task.FromResult(fileName);
    }

    public Task DeleteFileAsync(string s3Key, CancellationToken ct = default) => Task.CompletedTask;

    public Task<Stream> OpenReadAsync(string s3Key, CancellationToken ct = default)
        => Task.FromResult<Stream>(new MemoryStream(new byte[] { 1, 2, 3 }));
}

/// <summary>In-memory IRecordingRepository for the recording-ready consumer tests.</summary>
public sealed class FakeRecordingRepository : IRecordingRepository
{
    public List<SessionRecording> Store { get; } = new();

    public void Seed(SessionRecording recording) => Store.Add(recording);

    public Task AddAsync(SessionRecording recording, CancellationToken ct = default)
    {
        Store.Add(recording);
        return Task.CompletedTask;
    }

    public Task<SessionRecording?> GetBySessionIdAsync(Guid sessionId, CancellationToken ct = default)
        => Task.FromResult(Store.FirstOrDefault(r => r.SessionId == sessionId));

    public Task<SessionRecording?> GetByIdAsync(Guid recordingId, CancellationToken ct = default)
        => Task.FromResult(Store.FirstOrDefault(r => r.Id == recordingId));

    // Mirrors the real query: classroom filter + optional session/status, newest first, paged.
    public Task<(IEnumerable<SessionRecording> Items, int TotalCount)> ListByClassroomAsync(
        Guid classroomId, Guid? sessionId, ClassroomService.Domain.Enums.RecordingStatus? status,
        int page, int pageSize, CancellationToken ct = default)
    {
        var query = Store.Where(r => r.ClassroomId == classroomId);
        if (sessionId.HasValue) query = query.Where(r => r.SessionId == sessionId.Value);
        if (status.HasValue) query = query.Where(r => r.Status == status.Value);

        var ordered = query.OrderByDescending(r => r.CreatedAtUtc).ToList();
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult<(IEnumerable<SessionRecording>, int)>((items, ordered.Count));
    }

    public Task<List<SessionRecording>> GetStuckProcessingAsync(DateTime olderThanUtc, CancellationToken ct = default)
        => Task.FromResult(Store
            .Where(r => r.Status == ClassroomService.Domain.Enums.RecordingStatus.Processing && r.CreatedAtUtc < olderThanUtc)
            .ToList());

    public Task<List<SessionRecording>> GetOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default)
        => Task.FromResult(Store.Where(r => r.CreatedAtUtc < cutoffUtc).ToList());

    public Task RemoveAsync(SessionRecording recording, CancellationToken ct = default)
    {
        Store.Remove(recording);
        return Task.CompletedTask;
    }

    public Task<int> CountProcessingAsync(CancellationToken ct = default)
        => Task.FromResult(Store.Count(r => r.Status == ClassroomService.Domain.Enums.RecordingStatus.Processing));
}

/// <summary>Records recording metric calls so tests can assert instrumentation moved.</summary>
public sealed class FakeRecordingMetrics : IRecordingMetrics
{
    public int Completed { get; private set; }
    public long LastSizeBytes { get; private set; }
    public double LastEgressToAvailableSeconds { get; private set; }
    public int Failed { get; private set; }
    public int DownloadIssued { get; private set; }
    public List<string> Denials { get; } = new();
    public int Deleted { get; private set; }
    public List<string> ReconcileOutcomes { get; } = new();
    public int ProcessingCurrent { get; private set; }

    public void RecordingCompleted(long sizeBytes, double egressToAvailableSeconds)
    {
        Completed++;
        LastSizeBytes = sizeBytes;
        LastEgressToAvailableSeconds = egressToAvailableSeconds;
    }

    public void RecordingFailed() => Failed++;
    public void DownloadUrlIssued() => DownloadIssued++;
    public void DownloadAuthzDenied(string reason) => Denials.Add(reason);
    public void RecordingDeleted() => Deleted++;
    public void RecordingReconciled(string outcome) => ReconcileOutcomes.Add(outcome);
    public void SetProcessingCurrent(int count) => ProcessingCurrent = count;
}

/// <summary>Mock recording storage: records deleted keys, optionally throws to simulate a hard S3
/// failure. A missing object is a success (idempotent) — the fake simply records the key.</summary>
public sealed class FakeRecordingStorage : IRecordingStorage
{
    private readonly bool _throwOnDelete;
    public List<string> DeletedKeys { get; } = new();

    public FakeRecordingStorage(bool throwOnDelete = false) => _throwOnDelete = throwOnDelete;

    public Task DeleteObjectAsync(string objectKey, CancellationToken ct = default)
    {
        if (_throwOnDelete) throw new InvalidOperationException("S3 delete failed");
        DeletedKeys.Add(objectKey);
        return Task.CompletedTask;
    }
}

/// <summary>Settable clock for time-based reconcile/retention tests.</summary>
public sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
}

/// <summary>Fixed lifecycle settings for tests.</summary>
public sealed class FakeRecordingLifecycleSettings : IRecordingLifecycleSettings
{
    public int StuckProcessingMinutes { get; init; } = 30;
    public bool RetentionEnabled { get; init; }
    public int RetentionDays { get; init; }
}

/// <summary>
/// In-memory IMembershipRepository. It began as an enrollment check for the recording service, with
/// everything else throwing; MembershipService writes through the same port, so add, delete and
/// save are now real. A double that threw on Delete would have made "the removal was persisted"
/// untestable, and one that returned null from GetMembershipAsync regardless would have let a
/// removal that never found its row look identical to one that worked.
/// </summary>
public sealed class FakeMembershipRepository : IMembershipRepository
{
    private readonly List<ClassroomMembership> _enrollments = new();

    /// <summary>Writes that have not been saved yet — proof that persistence was actually asked for.</summary>
    public int SaveChangesCount { get; private set; }

    public IReadOnlyList<ClassroomMembership> All => _enrollments;

    public void Enroll(Guid classroomId, Guid studentId)
    {
        if (_enrollments.Any(e => e.ClassroomId == classroomId && e.StudentId == studentId)) return;
        _enrollments.Add(new ClassroomMembership
        {
            Id = Guid.NewGuid(),
            ClassroomId = classroomId,
            StudentId = studentId,
            JoinedAtUtc = DateTime.UtcNow,
        });
    }

    public Task<bool> IsEnrolledAsync(Guid classroomId, Guid studentId, CancellationToken ct = default)
        => Task.FromResult(_enrollments.Any(e => e.ClassroomId == classroomId && e.StudentId == studentId));

    /// <summary>
    /// The enrolled list, built from what was seeded. It used to return empty regardless, which was
    /// harmless until something started COUNTING members — the classroom tracking summary reports
    /// "n students enrolled", and a fake that always says none would pass a broken implementation.
    /// </summary>
    public Task<List<ClassroomMembership>> GetMembersWithDetailsAsync(Guid classroomId, CancellationToken ct = default)
        => Task.FromResult(_enrollments.Where(e => e.ClassroomId == classroomId).ToList());

    public Task<ClassroomMembership?> GetMembershipAsync(Guid classroomId, Guid studentId, CancellationToken ct = default)
        => Task.FromResult(_enrollments
            .FirstOrDefault(e => e.ClassroomId == classroomId && e.StudentId == studentId));

    public Task AddAsync(ClassroomMembership entity, CancellationToken ct = default)
    {
        if (entity.Id == Guid.Empty) entity.Id = Guid.NewGuid();
        _enrollments.Add(entity);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        _enrollments.RemoveAll(e => e.Id == id);
        return Task.CompletedTask;
    }

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCount++;
        return Task.FromResult(1);
    }

    public Task<ClassroomMembership?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(_enrollments.FirstOrDefault(e => e.Id == id));

    // IRepository<ClassroomMembership> surface — unused by these tests.
    public Task<(IEnumerable<ClassroomMembership> Items, int TotalCount)> GetPagedAsync(int page, int pageSize, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task UpdateAsync(ClassroomMembership entity, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>In-memory IUnitOfWork that counts SaveChanges so tests can await consume completion.</summary>
public sealed class FakeUnitOfWork : IUnitOfWork
{
    public int SaveChangesCount { get; private set; }

    public Task BeginTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task RollbackAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCount++;
        return Task.FromResult(1);
    }
}

/// <summary>
/// Records what was published, standing in for the MassTransit outbox.
/// </summary>
/// <remarks>
/// The real <c>IEventBus</c> is an <c>IPublishEndpoint</c> captured by <c>UseBusOutbox</c>, so a
/// publish is only durable once <c>SaveChangesAsync</c> runs. This fake records the ORDER of both,
/// because the bug it guards against is exactly that ordering: publishing without a subsequent
/// SaveChanges silently drops the message.
/// </remarks>
public sealed class RecordingEventBus : IEventBus
{
    public List<object> Published { get; } = new();

    public IEnumerable<T> PublishedOf<T>() => Published.OfType<T>();

    public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class
    {
        Published.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// IUnitOfWork that records the call sequence, so a test can assert that a publish was actually
/// committed rather than merely staged.
/// </summary>
public sealed class RecordingUnitOfWork : IUnitOfWork
{
    public List<string> Calls { get; } = new();
    public int SaveChangesCount { get; private set; }
    public bool Committed => Calls.Contains("Commit");
    public bool RolledBack => Calls.Contains("Rollback");

    public Task BeginTransactionAsync(CancellationToken ct = default)
    {
        Calls.Add("Begin");
        return Task.CompletedTask;
    }

    public Task CommitAsync(CancellationToken ct = default)
    {
        Calls.Add("Commit");
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken ct = default)
    {
        Calls.Add("Rollback");
        return Task.CompletedTask;
    }

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        Calls.Add("SaveChanges");
        SaveChangesCount++;
        return Task.FromResult(1);
    }
}

/// <summary>Mock pre-signed URL signer: captures the presign arguments and returns a fixed URL,
/// with expiry derived from the requested TTL so tests can assert TTL reflection. No S3, no network.</summary>
public sealed class FakeRecordingUrlSigner : IRecordingUrlSigner
{
    /// <summary>Deterministic base instant; returned expiry is Base + ttl.</summary>
    public static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public string ReturnUrl { get; set; } = "https://s3.example.test/intellilect-files/obj?X-Amz-Signature=abc123";
    public int Calls { get; private set; }
    public string? LastKey { get; private set; }
    public TimeSpan LastTtl { get; private set; }
    public string? LastContentDisposition { get; private set; }
    public string? LastContentType { get; private set; }

    public Task<PresignedUrl> GeneratePresignedGetUrlAsync(
        string objectKey, TimeSpan ttl, string contentDisposition, string? contentType, CancellationToken ct = default)
    {
        Calls++;
        LastKey = objectKey;
        LastTtl = ttl;
        LastContentDisposition = contentDisposition;
        LastContentType = contentType;
        return Task.FromResult(new PresignedUrl(ReturnUrl, Base + ttl));
    }
}

/// <summary>Fixed download settings for tests.</summary>
public sealed class FakeRecordingDownloadSettings : IRecordingDownloadSettings
{
    public int DownloadUrlTtlSeconds { get; init; } = 600;
}

/// <summary>In-memory ISummaryRepository for the summary consumer/service tests (S-4).</summary>
public sealed class FakeSummaryRepository : ISummaryRepository
{
    public List<SessionSummary> Store { get; } = new();

    public void Seed(SessionSummary summary) => Store.Add(summary);

    public Task AddAsync(SessionSummary summary, CancellationToken ct = default)
    {
        Store.Add(summary);
        return Task.CompletedTask;
    }

    public Task<SessionSummary?> GetBySessionIdAsync(Guid sessionId, CancellationToken ct = default)
        => Task.FromResult(Store.FirstOrDefault(s => s.SessionId == sessionId));

    public Task<SessionSummary?> GetByIdAsync(Guid summaryId, CancellationToken ct = default)
        => Task.FromResult(Store.FirstOrDefault(s => s.Id == summaryId));

    // Mirrors the real query: classroom filter + optional session, newest first, paged.
    public Task<(IEnumerable<SessionSummary> Items, int TotalCount)> ListByClassroomAsync(
        Guid classroomId, Guid? sessionId, int page, int pageSize, CancellationToken ct = default)
    {
        var query = Store.Where(s => s.ClassroomId == classroomId);
        if (sessionId.HasValue) query = query.Where(s => s.SessionId == sessionId.Value);

        var ordered = query.OrderByDescending(s => s.CreatedAtUtc).ToList();
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult<(IEnumerable<SessionSummary>, int)>((items, ordered.Count));
    }
}

/// <summary>Fixed summary download settings for tests.</summary>
public sealed class FakeSummaryDownloadSettings : ISummaryDownloadSettings
{
    public int DownloadUrlTtlSeconds { get; init; } = 600;
}

/// <summary>Records summary metric calls so tests can assert instrumentation moved.</summary>
public sealed class FakeSummaryMetrics : ISummaryMetrics
{
    public List<string> IssuedFormats { get; } = new();
    public List<string> Denials { get; } = new();
    public int AvailableIncrements { get; private set; }

    public void DownloadUrlIssued(string format) => IssuedFormats.Add(format);
    public void AuthzDenied(string reason) => Denials.Add(reason);
    public void AvailableIncrement() => AvailableIncrements++;
}

public static class TestMapper
{
    /// <summary>Real AutoMapper built from the production profile (no mocking).</summary>
    public static IMapper Create()
        // AutoMapper 14 added a required ILoggerFactory to this constructor. Null-logging here:
        // the tests assert on mappings, not on AutoMapper's diagnostics.
        => new MapperConfiguration(
            cfg => cfg.AddProfile<ClassroomProfile>(), NullLoggerFactory.Instance).CreateMapper();
}

/// <summary>
/// Upload limits with a small default size, so a test can exceed it with a few hundred bytes
/// instead of allocating the real 50 MB.
/// </summary>
public sealed class FakeUploadSettings : IUploadSettings
{
    public long MaxFileSizeBytes { get; init; } = 1024;

    public long MultipartOverheadBytes { get; init; } = 64;

    public IReadOnlyCollection<string> AllowedContentTypes { get; init; } =
        new[] { "application/pdf", "text/plain" };

    public IReadOnlyCollection<string> AllowedExtensions { get; init; } =
        new[] { "pdf", "txt", "md" };
}
