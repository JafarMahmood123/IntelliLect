using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.File;
using ClassroomService.Application.Services;
using ClassroomService.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassroomService.UnitTests;

// Unit tests for FileAdminService — the super-admin file list + delete for the knowledge-base view.
//   6أ -> missing reason. 7أ -> file missing. Delete order: store object -> de-index -> row, with the
//   de-index able to halt the delete before the row is removed (7هـ, resumable).
public class FileAdminServiceTests
{
    [Fact]
    public async Task Delete_WithoutReason_Throws()
    {
        var repo = new FakeFileRepo(File("f.pdf"));
        var sut = Build(repo, new FakeStorage(), new FakeKnowledge());

        await Assert.ThrowsAsync<ArgumentException>(() => sut.DeleteFileAsync(repo.File!.Id, "  ")); // 6أ
    }

    [Fact]
    public async Task Delete_WhenFileMissing_ThrowsNotFound()
    {
        var repo = new FakeFileRepo(null);
        var sut = Build(repo, new FakeStorage(), new FakeKnowledge());

        await Assert.ThrowsAsync<KeyNotFoundException>(() => sut.DeleteFileAsync(Guid.NewGuid(), "cleanup")); // 7أ
    }

    [Fact]
    public async Task Delete_HappyPath_DeletesObjectThenDeIndexesThenRow()
    {
        var file = File("f.pdf");
        var repo = new FakeFileRepo(file);
        var storage = new FakeStorage();
        var knowledge = new FakeKnowledge();
        var sut = Build(repo, storage, knowledge);

        var result = await sut.DeleteFileAsync(file.Id, "no longer needed");

        Assert.Contains(file.S3Key, storage.Deleted);
        Assert.Equal(file.Id, knowledge.LastDeletedFileId);
        Assert.True(repo.Removed);

        // Ordering: object -> de-index -> row.
        Assert.True(repo.Timeline.IndexOf("obj") < repo.Timeline.IndexOf("deindex"));
        Assert.True(repo.Timeline.IndexOf("deindex") < repo.Timeline.IndexOf("row"));

        Assert.True(result.StorageDeleted);
        Assert.True(result.DeIndexed);
    }

    [Fact]
    public async Task Delete_WhenDeIndexFails_HaltsBeforeRemovingRow()
    {
        // 7هـ: the de-index step throws, so the metadata row survives for a resumable re-run.
        var file = File("f.pdf");
        var repo = new FakeFileRepo(file);
        var storage = new FakeStorage();
        var knowledge = new FakeKnowledge { ThrowOnDelete = true };
        var sut = Build(repo, storage, knowledge);

        await Assert.ThrowsAsync<HttpRequestException>(() => sut.DeleteFileAsync(file.Id, "cleanup"));

        Assert.Contains(file.S3Key, storage.Deleted); // object already gone (idempotent on re-run)
        Assert.False(repo.Removed);                   // row NOT removed
    }

    [Fact]
    public async Task GetFiles_ReturnsPagedRegistryRows()
    {
        var repo = new FakeFileRepo(null)
        {
            Paged = (new List<AdminFileRow>
            {
                new(Guid.NewGuid(), "a.pdf", "application/pdf", 100, Guid.NewGuid()),
            }, 1),
        };
        var sut = Build(repo, new FakeStorage(), new FakeKnowledge());

        var page = await sut.GetFilesAsync(null, null, 1, 20);

        Assert.Single(page.Items);
        Assert.Equal(1, page.TotalCount);
    }

    // --- helpers ---------------------------------------------------------------

    private static ClassroomFile File(string name) => new()
    {
        Id = Guid.NewGuid(),
        FileName = name,
        S3Key = $"classrooms/x/{name}",
        ContentType = "application/pdf",
        SizeBytes = 123,
        ClassroomId = Guid.NewGuid(),
    };

    private static FileAdminService Build(FakeFileRepo repo, FakeStorage storage, FakeKnowledge knowledge)
    {
        // Wire the fakes to the repo's shared timeline so ordering across the three steps is observable.
        storage.Repo = repo;
        knowledge.Repo = repo;
        return new FileAdminService(repo, storage, knowledge, NullLogger<FileAdminService>.Instance);
    }

    private sealed class FakeFileRepo : IFileAdminRepository
    {
        public ClassroomFile? File;
        public bool Removed;
        public (List<AdminFileRow> Items, int Total) Paged = (new(), 0);
        public readonly List<string> Timeline = new();

        public FakeFileRepo(ClassroomFile? file) => File = file;

        public Task<(List<AdminFileRow> Items, int TotalCount)> GetPagedAsync(
            string? search, Guid? classroomId, int page, int pageSize, CancellationToken ct = default)
            => Task.FromResult(Paged);

        public Task<List<AdminFileRow>> GetByIdsAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken ct = default)
            => Task.FromResult(new List<AdminFileRow>());

        public Task<ClassroomFile?> GetByIdAsync(Guid fileId, CancellationToken ct = default)
            => Task.FromResult(File is not null && File.Id == fileId ? File : null);

        public void Remove(ClassroomFile file)
        {
            Removed = true;
            Timeline.Add("row");
        }

        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void RecordObject() => Timeline.Add("obj");
        public void RecordDeindex() => Timeline.Add("deindex");
    }

    private sealed class FakeStorage : IFileStorageService
    {
        public readonly List<string> Deleted = new();
        public FakeFileRepo? Repo;

        public Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType, CancellationToken ct = default)
            => Task.FromResult("k");

        public Task DeleteFileAsync(string s3Key, CancellationToken ct = default)
        {
            Deleted.Add(s3Key);
            Repo?.RecordObject();
            return Task.CompletedTask;
        }

        public Task<Stream> OpenReadAsync(string s3Key, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream());
    }

    private sealed class FakeKnowledge : IKnowledgeInternalClient
    {
        public bool ThrowOnDelete;
        public Guid LastDeletedFileId;
        public FakeFileRepo? Repo;

        public Task NotifyFileDeletedAsync(Guid fileId, CancellationToken ct = default)
        {
            if (ThrowOnDelete) throw new HttpRequestException("KnowledgeService is unreachable");
            LastDeletedFileId = fileId;
            Repo?.RecordDeindex();
            return Task.CompletedTask;
        }

        public Task NotifyFileUploadedAsync(Guid fileId, Guid classroomId, string s3Key, string fileName, string contentType, long sizeBytes, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeIndexClassroomAsync(Guid classroomId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetIndexingStatusAsync(Guid fileId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<Application.Models.KnowledgeAnswerResult> GetAnswerAsync(Guid classroomId, string question, CancellationToken ct = default)
            => Task.FromResult(new Application.Models.KnowledgeAnswerResult(string.Empty, new List<Application.Models.KnowledgeAnswerSource>()));
        public Task<bool> TriggerSummaryAsync(Guid sessionId, Guid classroomId, CancellationToken ct = default) => Task.FromResult(true);
    }
}
