using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Output;
using ClassroomService.Application.Exceptions;
using ClassroomService.Application.Services;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassroomService.UnitTests;

// Unit tests for OutputAdminService — the super-admin recordings/summaries delete.
//   4أ -> missing reason. 5أ -> output missing. 5ب -> session live.
//   Step 6 -> mark PendingDeletion, delete object(s), delete row.
//   6ب -> a file-delete failure halts with the output left PendingDeletion (resumable).
public class OutputAdminServiceTests
{
    [Fact]
    public async Task DeleteRecording_WithoutReason_Throws()
    {
        var repo = new FakeRepo { Recording = Recording() };
        var sut = Build(repo, new FakeStorage());

        await Assert.ThrowsAsync<ArgumentException>(() => sut.DeleteRecordingAsync(repo.Recording!.Id, " ")); // 4أ
    }

    [Fact]
    public async Task DeleteRecording_WhenMissing_ThrowsNotFound()
    {
        var sut = Build(new FakeRepo(), new FakeStorage());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => sut.DeleteRecordingAsync(Guid.NewGuid(), "cleanup")); // 5أ
    }

    [Fact]
    public async Task DeleteRecording_WhenSessionLive_ThrowsConflict()
    {
        var rec = Recording();
        var repo = new FakeRepo { Recording = rec, LiveSessionId = rec.SessionId };
        var sut = Build(repo, new FakeStorage());

        await Assert.ThrowsAsync<ConflictException>(() => sut.DeleteRecordingAsync(rec.Id, "cleanup")); // 5ب
        Assert.False(repo.RecordingRemoved);
        Assert.NotEqual(RecordingStatus.PendingDeletion, rec.Status);
    }

    [Fact]
    public async Task DeleteRecording_HappyPath_MarksPendingThenDeletesObjectThenRow()
    {
        var rec = Recording();
        rec.S3Key = "rec/1.mp4";
        var repo = new FakeRepo { Recording = rec };
        var storage = new FakeStorage();
        var sut = Build(repo, storage);

        var result = await sut.DeleteRecordingAsync(rec.Id, "no longer needed");

        Assert.Contains(RecordingStatus.PendingDeletion, repo.RecordingStatusHistory); // step 6 marker
        Assert.Contains("rec/1.mp4", storage.Deleted);
        Assert.True(repo.RecordingRemoved);
        Assert.True(result.StorageDeleted);
        Assert.Equal("Recording", result.Type);
    }

    [Fact]
    public async Task DeleteRecording_WhenObjectDeleteFails_LeavesPendingDeletion()
    {
        var rec = Recording();
        rec.S3Key = "rec/1.mp4";
        var repo = new FakeRepo { Recording = rec };
        var storage = new FakeStorage { Throw = true };
        var sut = Build(repo, storage);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.DeleteRecordingAsync(rec.Id, "cleanup")); // 6ب

        Assert.Equal(RecordingStatus.PendingDeletion, rec.Status); // stays PendingDeletion
        Assert.False(repo.RecordingRemoved);                       // row survives for a resumable re-run
    }

    [Fact]
    public async Task DeleteRecording_WithNullKey_SkipsStorageButDeletesRow()
    {
        // A recording that never became Available has a null S3 key — nothing to delete in the store (6أ-adjacent).
        var rec = Recording();
        rec.S3Key = null;
        var repo = new FakeRepo { Recording = rec };
        var storage = new FakeStorage();
        var sut = Build(repo, storage);

        var result = await sut.DeleteRecordingAsync(rec.Id, "cleanup");

        Assert.Empty(storage.Deleted);
        Assert.True(repo.RecordingRemoved);
        Assert.True(result.StorageDeleted);
    }

    [Fact]
    public async Task DeleteSummary_HappyPath_DeletesBothObjectsThenRow()
    {
        var summary = Summary();
        summary.MdS3Key = "sum/1.md";
        summary.PdfS3Key = "sum/1.pdf";
        var repo = new FakeRepo { Summary = summary };
        var storage = new FakeStorage();
        var sut = Build(repo, storage);

        var result = await sut.DeleteSummaryAsync(summary.Id, "cleanup");

        Assert.Contains("sum/1.md", storage.Deleted);
        Assert.Contains("sum/1.pdf", storage.Deleted);
        Assert.True(repo.SummaryRemoved);
        Assert.Equal("Summary", result.Type);
    }

    [Fact]
    public async Task DeleteSummary_WhenMissing_ThrowsNotFound()
    {
        var sut = Build(new FakeRepo(), new FakeStorage());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => sut.DeleteSummaryAsync(Guid.NewGuid(), "cleanup")); // 5أ
    }

    // --- helpers ---------------------------------------------------------------

    private static SessionRecording Recording() => new()
    {
        Id = Guid.NewGuid(),
        SessionId = Guid.NewGuid(),
        ClassroomId = Guid.NewGuid(),
        EgressId = "egress",
        Status = RecordingStatus.Available,
        SizeBytes = 2048,
    };

    private static SessionSummary Summary() => new()
    {
        Id = Guid.NewGuid(),
        SessionId = Guid.NewGuid(),
        ClassroomId = Guid.NewGuid(),
        Status = SummaryStatus.Available,
    };

    private static OutputAdminService Build(FakeRepo repo, FakeStorage storage)
        => new(repo, storage, new RecordingEventBus(), NullLogger<OutputAdminService>.Instance);

    private sealed class FakeRepo : IOutputAdminRepository
    {
        public SessionRecording? Recording;
        public SessionSummary? Summary;
        public Guid? LiveSessionId;
        public bool RecordingRemoved;
        public bool SummaryRemoved;
        public readonly List<RecordingStatus> RecordingStatusHistory = new();

        public Task<(List<AdminOutputRow> Items, int TotalCount)> GetOutputsPagedAsync(
            string? search, string? type, string? status, Guid? classroomId, int page, int pageSize, CancellationToken ct = default)
            => Task.FromResult((new List<AdminOutputRow>(), 0));

        public Task<SessionRecording?> GetRecordingAsync(Guid recordingId, CancellationToken ct = default)
            => Task.FromResult(Recording is not null && Recording.Id == recordingId ? Recording : null);

        public Task<SessionSummary?> GetSummaryAsync(Guid summaryId, CancellationToken ct = default)
            => Task.FromResult(Summary is not null && Summary.Id == summaryId ? Summary : null);

        public Task<bool> IsSessionLiveAsync(Guid sessionId, CancellationToken ct = default)
            => Task.FromResult(LiveSessionId == sessionId);

        public void RemoveRecording(SessionRecording recording) => RecordingRemoved = true;
        public void RemoveSummary(SessionSummary summary) => SummaryRemoved = true;

        public Task SaveChangesAsync(CancellationToken ct = default)
        {
            if (Recording is not null) RecordingStatusHistory.Add(Recording.Status);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStorage : IRecordingStorage
    {
        public readonly List<string> Deleted = new();
        public bool Throw;

        public Task DeleteObjectAsync(string objectKey, CancellationToken ct = default)
        {
            if (Throw) throw new InvalidOperationException("storage delete failed");
            Deleted.Add(objectKey);
            return Task.CompletedTask;
        }
    }
}
