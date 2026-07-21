using UserManagementService.Application.Abstractions;
using UserManagementService.Application.DTOs.Knowledge;
using UserManagementService.Application.KnowledgeAdministration;

namespace UserManagementService.UnitTests.KnowledgeAdministration;

// Unit tests for KnowledgeAdminService — the super-admin content/knowledge-base gateway.
//   List: ClassroomService drives (no status filter) with KnowledgeService status enrichment that
//         degrades to "unavailable" when it fails (3أ); KnowledgeService drives when a status filter
//         is set. Reindex/delete require a reason (6أ).
public class KnowledgeAdminServiceTests
{
    [Fact]
    public async Task GetFiles_NoStatusFilter_ClassroomDrivesAndEnrichesStatus()
    {
        var fileId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();
        var classroom = new FakeClassroomClient
        {
            FilePage = new AdminFilePage(
                new[] { new AdminFile(fileId, "notes.pdf", "application/pdf", 500, classroomId) }, 1, 1, 20),
            Names = new[] { new ClassroomName(classroomId, "Physics 101") },
        };
        var knowledge = new FakeKnowledgeClient
        {
            StatusBatch = new[] { new KnowledgeDocumentItem(fileId, classroomId, "notes.pdf", "application/pdf", 500, "Done", 0, 7) },
        };
        var sut = new KnowledgeAdminService(classroom, knowledge);

        var result = await sut.GetFilesAsync(new SearchFilesRequest { Page = 1, PageSize = 20 });

        var item = Assert.Single(result.Items);
        Assert.Equal("notes.pdf", item.FileName);
        Assert.Equal("Physics 101", item.ClassName);
        Assert.Equal("Done", item.Status);
        Assert.Equal(7, item.ChunkCount);
        Assert.False(result.IndexingUnavailable);
    }

    [Fact]
    public async Task GetFiles_WhenStatusBatchFails_DegradesGracefully()
    {
        var fileId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();
        var classroom = new FakeClassroomClient
        {
            FilePage = new AdminFilePage(
                new[] { new AdminFile(fileId, "notes.pdf", "application/pdf", 500, classroomId) }, 1, 1, 20),
        };
        var knowledge = new FakeKnowledgeClient { ThrowOnStatusBatch = true };
        var sut = new KnowledgeAdminService(classroom, knowledge);

        var result = await sut.GetFilesAsync(new SearchFilesRequest());

        // 3أ: the list still renders; status is unknown and the flag is set.
        var item = Assert.Single(result.Items);
        Assert.Null(item.Status);
        Assert.True(result.IndexingUnavailable);
    }

    [Fact]
    public async Task GetFiles_WithStatusFilter_KnowledgeDrivesAndEnrichesRegistry()
    {
        var fileId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();
        var knowledge = new FakeKnowledgeClient
        {
            DocPage = new KnowledgeDocumentPage(
                new[] { new KnowledgeDocumentItem(fileId, classroomId, "ks-name.pdf", "application/pdf", 999, "Failed", 3, 0) }, 1, 1, 20),
        };
        var classroom = new FakeClassroomClient
        {
            FilesByIds = new[] { new AdminFile(fileId, "authoritative.pdf", "application/pdf", 500, classroomId) },
            Names = new[] { new ClassroomName(classroomId, "Physics 101") },
        };
        var sut = new KnowledgeAdminService(classroom, knowledge);

        var result = await sut.GetFilesAsync(new SearchFilesRequest { Status = "Failed" });

        var item = Assert.Single(result.Items);
        Assert.Equal("Failed", item.Status);
        Assert.Equal("authoritative.pdf", item.FileName); // CS registry wins over KS denorm
        Assert.Equal(500, item.SizeBytes);
        Assert.Equal("Physics 101", item.ClassName);
    }

    [Fact]
    public async Task GetFileDetail_WhenMissing_ReturnsNull()
    {
        var sut = new KnowledgeAdminService(new FakeClassroomClient(), new FakeKnowledgeClient { Detail = null });
        Assert.Null(await sut.GetFileDetailAsync(Guid.NewGuid())); // 7أ
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReindexFile_WithoutReason_Throws(string reason)
    {
        var knowledge = new FakeKnowledgeClient();
        var sut = new KnowledgeAdminService(new FakeClassroomClient(), knowledge);

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.ReindexFileAsync(Guid.NewGuid(), new ReindexFileRequest(reason))); // 6أ
        Assert.False(knowledge.ReindexFileCalled);
    }

    [Fact]
    public async Task ReindexFile_WithReason_Delegates()
    {
        var knowledge = new FakeKnowledgeClient();
        var sut = new KnowledgeAdminService(new FakeClassroomClient(), knowledge);

        await sut.ReindexFileAsync(Guid.NewGuid(), new ReindexFileRequest("stale index"));
        Assert.True(knowledge.ReindexFileCalled);
    }

    [Fact]
    public async Task ReindexClassroom_WithoutReason_Throws()
    {
        var knowledge = new FakeKnowledgeClient();
        var sut = new KnowledgeAdminService(new FakeClassroomClient(), knowledge);

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.ReindexClassroomAsync(Guid.NewGuid(), new ReindexClassroomRequest(true, "")));
    }

    [Fact]
    public async Task ReindexClassroom_PassesFailedOnlyAndMapsResult()
    {
        var classroomId = Guid.NewGuid();
        var knowledge = new FakeKnowledgeClient
        {
            BulkResult = new BulkReindexResult(classroomId, 5, 3, 2),
        };
        var sut = new KnowledgeAdminService(new FakeClassroomClient(), knowledge);

        var result = await sut.ReindexClassroomAsync(classroomId, new ReindexClassroomRequest(true, "cleanup"));

        Assert.True(knowledge.LastFailedOnly);
        Assert.Equal(3, result.Enqueued);
        Assert.Equal(2, result.Skipped);
    }

    [Fact]
    public async Task DeleteFile_WithoutReason_Throws()
    {
        var classroom = new FakeClassroomClient();
        var sut = new KnowledgeAdminService(classroom, new FakeKnowledgeClient());

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.DeleteFileAsync(Guid.NewGuid(), new DeleteFileAdminRequest("  "))); // 6أ
        Assert.False(classroom.DeleteFileCalled);
    }

    [Fact]
    public async Task DeleteFile_WithReason_DelegatesToClassroomService()
    {
        var fileId = Guid.NewGuid();
        var classroom = new FakeClassroomClient
        {
            FileDeletion = new FileDeletionResult(fileId, true, true),
        };
        var sut = new KnowledgeAdminService(classroom, new FakeKnowledgeClient());

        var result = await sut.DeleteFileAsync(fileId, new DeleteFileAdminRequest("  no longer needed  "));

        Assert.True(classroom.DeleteFileCalled);
        Assert.Equal("no longer needed", classroom.LastDeleteReason); // trimmed
        Assert.True(result.StorageDeleted);
        Assert.True(result.DeIndexed);
    }
}

// ----- fakes ------------------------------------------------------------------

internal sealed class FakeKnowledgeClient : IKnowledgeAdminClient
{
    public IReadOnlyList<KnowledgeDocumentItem> StatusBatch { get; set; } = Array.Empty<KnowledgeDocumentItem>();
    public bool ThrowOnStatusBatch { get; set; }
    public KnowledgeDocumentPage DocPage { get; set; } = new(Array.Empty<KnowledgeDocumentItem>(), 0, 1, 20);
    public KnowledgeDocumentDetail? Detail { get; set; } = new(Guid.NewGuid(), Guid.NewGuid(), "f", "t", 1, "Done", 0, 1, null);
    public KnowledgeStatsResult Stats { get; set; } = new(null, 0, new Dictionary<string, int>(), 0, 0, 0);
    public bool ReindexFileCalled { get; private set; }
    public BulkReindexResult BulkResult { get; set; } = new(Guid.NewGuid(), 0, 0, 0);
    public bool LastFailedOnly { get; private set; }

    public Task<KnowledgeDocumentPage> ListDocumentsAsync(int page, int pageSize, string? status, Guid? classroomId, string? search, CancellationToken ct = default)
        => Task.FromResult(DocPage);

    public Task<IReadOnlyList<KnowledgeDocumentItem>> GetStatusBatchAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken ct = default)
    {
        if (ThrowOnStatusBatch) throw new HttpRequestException("KnowledgeService unreachable");
        return Task.FromResult(StatusBatch);
    }

    public Task<KnowledgeDocumentDetail?> GetDocumentDetailAsync(Guid fileId, CancellationToken ct = default)
        => Task.FromResult(Detail);

    public Task<KnowledgeStatsResult> GetStatsAsync(Guid? classroomId, CancellationToken ct = default)
        => Task.FromResult(Stats);

    public Task ReindexFileAsync(Guid fileId, CancellationToken ct = default)
    {
        ReindexFileCalled = true;
        return Task.CompletedTask;
    }

    public Task<BulkReindexResult> ReindexClassroomAsync(Guid classroomId, bool failedOnly, CancellationToken ct = default)
    {
        LastFailedOnly = failedOnly;
        return Task.FromResult(BulkResult);
    }
}

internal sealed class FakeClassroomClient : IClassroomInternalClient
{
    public AdminFilePage FilePage { get; set; } = new(Array.Empty<AdminFile>(), 0, 1, 20);
    public IReadOnlyList<AdminFile> FilesByIds { get; set; } = Array.Empty<AdminFile>();
    public IReadOnlyList<ClassroomName> Names { get; set; } = Array.Empty<ClassroomName>();
    public bool DeleteFileCalled { get; private set; }
    public string? LastDeleteReason { get; private set; }
    public FileDeletionResult FileDeletion { get; set; } = new(Guid.NewGuid(), true, true);

    public Task<AdminFilePage> GetFilesAsync(int page, int pageSize, string? search, Guid? classroomId, CancellationToken ct = default)
        => Task.FromResult(FilePage);

    public Task<IReadOnlyList<AdminFile>> GetFilesByIdsAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken ct = default)
        => Task.FromResult(FilesByIds);

    public Task<IReadOnlyList<ClassroomName>> GetClassroomNamesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        => Task.FromResult(Names);

    public Task<FileDeletionResult> DeleteFileAsync(Guid fileId, string reason, CancellationToken ct = default)
    {
        DeleteFileCalled = true;
        LastDeleteReason = reason;
        return Task.FromResult(FileDeletion);
    }

    // Unused by KnowledgeAdminService.
    public Task<UserClassrooms> GetUserClassroomsAsync(Guid userId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AdminClassroomPage> GetClassroomsAsync(int page, int pageSize, string? search, Guid? teacherId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AdminClassroom?> GetClassroomByIdAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Guid> CreateClassroomAsync(Guid teacherId, string name, string description, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpdateClassroomAsync(Guid id, string name, string description, long version, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ClassroomTeacherChange> ChangeClassroomTeacherAsync(Guid id, Guid newTeacherId, long version, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ClassroomDeletionImpact?> GetClassroomDeletionImpactAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ClassroomDeletionResult> DeleteClassroomAsync(Guid id, string reason, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AdminSessionPage> GetSessionsAsync(int page, int pageSize, string? search, string? status, Guid? classroomId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ForceEndResult> ForceEndSessionAsync(Guid sessionId, string reason, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<SessionDeletionImpact?> GetSessionDeletionImpactAsync(Guid sessionId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<SessionDeletionResult> DeleteSessionAsync(Guid sessionId, string reason, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AdminOutputPage> GetOutputsAsync(int page, int pageSize, string? search, string? type, string? status, Guid? classroomId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<OutputDeletionResult> DeleteRecordingAsync(Guid recordingId, string reason, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<OutputDeletionResult> DeleteSummaryAsync(Guid summaryId, string reason, CancellationToken ct = default) => throw new NotImplementedException();
}
