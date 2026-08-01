using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Classroom;
using ClassroomService.Application.Exceptions;
using ClassroomService.Application.Services;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassroomService.UnitTests;

// Unit tests for ClassroomDeletionService — the super-admin "حذف فصل دراسي" orchestration.
//   4أ -> missing reason. 5أ -> classroom missing. 5ب -> a live session blocks deletion.
//   Step 6 -> mark PendingDeletion, then purge objects-before-rows in phase order.
//   6أ -> a phase fails (here de-index): the classroom stays PendingDeletion and is NOT removed,
//         so a re-run resumes.
public class ClassroomDeletionServiceTests
{
    [Fact]
    public async Task Delete_WithoutReason_ThrowsArgumentException()
    {
        var (repo, storage, files, knowledge) = Fakes();
        repo.Seed(ActiveClassroom());
        var sut = Build(repo, storage, files, knowledge);

        // 4أ.
        await Assert.ThrowsAsync<ArgumentException>(() => sut.DeleteAsync(repo.Classroom!.Id, "   "));
        Assert.Equal(ClassroomStatus.Active, repo.Classroom!.Status); // untouched
    }

    [Fact]
    public async Task Delete_WhenClassroomMissing_ThrowsKeyNotFound()
    {
        var (repo, storage, files, knowledge) = Fakes();
        var sut = Build(repo, storage, files, knowledge);

        // 5أ.
        await Assert.ThrowsAsync<KeyNotFoundException>(() => sut.DeleteAsync(Guid.NewGuid(), "done"));
    }

    [Fact]
    public async Task Delete_WhenLiveSession_ThrowsConflictAndDoesNotMarkPending()
    {
        var (repo, storage, files, knowledge) = Fakes();
        repo.Seed(ActiveClassroom());
        repo.HasLiveSession = true;
        var sut = Build(repo, storage, files, knowledge);

        // 5ب.
        await Assert.ThrowsAsync<ConflictException>(() => sut.DeleteAsync(repo.Classroom!.Id, "done"));
        Assert.Equal(ClassroomStatus.Active, repo.Classroom!.Status);
        Assert.False(repo.ClassroomRemoved);
    }

    [Fact]
    public async Task Delete_HappyPath_PurgesEverythingAndRemovesClassroom()
    {
        var (repo, storage, files, knowledge) = Fakes();
        var classroom = ActiveClassroom();
        repo.Seed(classroom);
        repo.Recordings.Add(new SessionRecording { Id = Guid.NewGuid(), ClassroomId = classroom.Id, S3Key = "rec/1.mp4" });
        repo.Summaries.Add(new SessionSummary { Id = Guid.NewGuid(), ClassroomId = classroom.Id, MdS3Key = "sum/1.md", PdfS3Key = "sum/1.pdf" });
        repo.Files.Add(new ClassroomFile { Id = Guid.NewGuid(), ClassroomId = classroom.Id, S3Key = "classrooms/x/f.pdf", FileName = "f.pdf", ContentType = "application/pdf" });
        repo.SessionsCount = 4;
        repo.MembershipsCount = 7;

        var sut = Build(repo, storage, files, knowledge);
        var result = await sut.DeleteAsync(classroom.Id, "course ended");

        // Objects were deleted: recording via object storage, summary md+pdf via object storage, file via file storage.
        Assert.Contains("rec/1.mp4", storage.Deleted);
        Assert.Contains("sum/1.md", storage.Deleted);
        Assert.Contains("sum/1.pdf", storage.Deleted);
        Assert.Contains("classrooms/x/f.pdf", files.Deleted);

        // Rows removed + classroom de-indexed + classroom row removed.
        Assert.True(repo.ClassroomRemoved);
        Assert.Equal(1, knowledge.DeIndexCalls);
        Assert.Equal(classroom.Id, knowledge.LastDeIndexedClassroomId);

        // Counts reported (step 8).
        Assert.Equal(1, result.RecordingsDeleted);
        Assert.Equal(1, result.SummariesDeleted);
        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(4, result.SessionsDeleted);
        Assert.Equal(7, result.MembershipsDeleted);
    }

    [Fact]
    public async Task Delete_DeletesObjectBeforeRow()
    {
        var (repo, storage, files, knowledge) = Fakes();
        var classroom = ActiveClassroom();
        repo.Seed(classroom);
        var rec = new SessionRecording { Id = Guid.NewGuid(), ClassroomId = classroom.Id, S3Key = "rec/1.mp4" };
        repo.Recordings.Add(rec);
        // Share one timeline between storage and repo so the ordering is a global sequence.
        var timeline = new List<string>();
        repo.Timeline = timeline;
        storage.Timeline = timeline;
        var sut = Build(repo, storage, files, knowledge);

        await sut.DeleteAsync(classroom.Id, "done");

        // The S3 object delete was recorded before the row removal (objects-before-rows invariant).
        var objectOrder = timeline.IndexOf("obj:rec/1.mp4");
        var rowOrder = timeline.IndexOf($"recording:{rec.Id}");
        Assert.True(objectOrder >= 0 && rowOrder >= 0 && objectOrder < rowOrder);
    }

    [Fact]
    public async Task Delete_RecordingWithoutS3Key_SkipsStorageButRemovesRow()
    {
        var (repo, storage, files, knowledge) = Fakes();
        var classroom = ActiveClassroom();
        repo.Seed(classroom);
        var rec = new SessionRecording { Id = Guid.NewGuid(), ClassroomId = classroom.Id, S3Key = null };
        repo.Recordings.Add(rec);
        var sut = Build(repo, storage, files, knowledge);

        var result = await sut.DeleteAsync(classroom.Id, "done");

        Assert.Empty(storage.Deleted);
        Assert.Contains($"recording:{rec.Id}", repo.RemoveOrder);
        Assert.Equal(1, result.RecordingsDeleted);
    }

    [Fact]
    public async Task Delete_WhenDeIndexFails_LeavesClassroomPendingDeletion()
    {
        var (repo, storage, files, knowledge) = Fakes();
        var classroom = ActiveClassroom();
        repo.Seed(classroom);
        repo.Files.Add(new ClassroomFile { Id = Guid.NewGuid(), ClassroomId = classroom.Id, S3Key = "classrooms/x/f.pdf", FileName = "f", ContentType = "text/plain" });
        knowledge.ThrowOnDeIndex = true;
        var sut = Build(repo, storage, files, knowledge);

        // 6أ: the de-index phase throws; the deletion must halt.
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.DeleteAsync(classroom.Id, "done"));

        // Classroom stays PendingDeletion and is NOT removed, so a re-run can resume.
        Assert.Equal(ClassroomStatus.PendingDeletion, classroom.Status);
        Assert.False(repo.ClassroomRemoved);
        // The file object was already deleted before the failure (partial progress is kept).
        Assert.Contains("classrooms/x/f.pdf", files.Deleted);
    }

    [Fact]
    public async Task Delete_MarksPendingDeletionBeforePurging()
    {
        var (repo, storage, files, knowledge) = Fakes();
        var classroom = ActiveClassroom();
        repo.Seed(classroom);
        var sut = Build(repo, storage, files, knowledge);

        await sut.DeleteAsync(classroom.Id, "done");

        // The first status the classroom moved through was PendingDeletion (step 6).
        Assert.Contains(ClassroomStatus.PendingDeletion, repo.StatusHistory);
    }

    [Fact]
    public async Task Delete_OnResume_DoesNotFailAndCompletes()
    {
        var (repo, storage, files, knowledge) = Fakes();
        var classroom = ActiveClassroom();
        classroom.Status = ClassroomStatus.PendingDeletion; // a previous run already marked it
        repo.Seed(classroom);
        var sut = Build(repo, storage, files, knowledge);

        var result = await sut.DeleteAsync(classroom.Id, "resume");

        Assert.True(repo.ClassroomRemoved);
        Assert.Equal(1, knowledge.DeIndexCalls);
        Assert.NotNull(result);
    }

    // --- helpers ---------------------------------------------------------------

    private static Classroom ActiveClassroom() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Physics 101",
        Description = "d",
        TeacherId = Guid.NewGuid(),
        Status = ClassroomStatus.Active,
    };

    private static (FakeDeletionRepo, FakeObjectStorage, FakeFileStorage, FakeKnowledge) Fakes()
        => (new FakeDeletionRepo(), new FakeObjectStorage(), new FakeFileStorage(), new FakeKnowledge());

    private static ClassroomDeletionService Build(
        FakeDeletionRepo repo, FakeObjectStorage storage, FakeFileStorage files, FakeKnowledge knowledge)
        => new(repo, files, storage, knowledge, new FakeLiveAssistant(), NullLogger<ClassroomDeletionService>.Instance);

    private sealed class FakeDeletionRepo : IClassroomDeletionRepository
    {
        public Classroom? Classroom;
        public bool HasLiveSession;
        public bool ClassroomRemoved;
        public int SessionsCount;
        public int MembershipsCount;
        public readonly List<SessionRecording> Recordings = new();
        public readonly List<SessionSummary> Summaries = new();
        public readonly List<ClassroomFile> Files = new();
        public readonly List<string> RemoveOrder = new();
        public readonly List<ClassroomStatus> StatusHistory = new();
        // Optional shared timeline (set by the ordering test) recording object/row events in sequence.
        public List<string>? Timeline;

        public void Seed(Classroom c) => Classroom = c;

        public Task<ClassroomDeletionImpact?> GetImpactAsync(Guid classroomId, CancellationToken ct = default)
            => Task.FromResult<ClassroomDeletionImpact?>(null);

        public Task<Classroom?> GetTrackedAsync(Guid classroomId, CancellationToken ct = default)
            => Task.FromResult(Classroom is not null && Classroom.Id == classroomId ? Classroom : null);

        public Task<bool> HasLiveSessionAsync(Guid classroomId, CancellationToken ct = default)
            => Task.FromResult(HasLiveSession);

        public Task<List<SessionRecording>> GetRecordingsAsync(Guid classroomId, CancellationToken ct = default)
            => Task.FromResult(Recordings.ToList());

        public Task<List<SessionSummary>> GetSummariesAsync(Guid classroomId, CancellationToken ct = default)
            => Task.FromResult(Summaries.ToList());

        public Task<List<ClassroomFile>> GetFilesAsync(Guid classroomId, CancellationToken ct = default)
            => Task.FromResult(Files.ToList());

        public void RemoveRecording(SessionRecording recording) => Record($"recording:{recording.Id}");
        public void RemoveSummary(SessionSummary summary) => Record($"summary:{summary.Id}");
        public void RemoveFile(ClassroomFile file) => Record($"file:{file.Id}");
        public void RemoveClassroom(Classroom classroom) => ClassroomRemoved = true;

        private void Record(string evt)
        {
            RemoveOrder.Add(evt);
            Timeline?.Add(evt);
        }

        public Task<int> DeleteSessionsAsync(Guid classroomId, CancellationToken ct = default)
            => Task.FromResult(SessionsCount);

        public Task<int> DeleteMembershipsAsync(Guid classroomId, CancellationToken ct = default)
            => Task.FromResult(MembershipsCount);

        public Task SaveChangesAsync(CancellationToken ct = default)
        {
            if (Classroom is not null)
            {
                StatusHistory.Add(Classroom.Status);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLiveAssistant : ILiveAssistantInternalClient
    {
        public Task<int?> GetTranscriptSegmentCountAsync(Guid sessionId, CancellationToken ct = default)
            => Task.FromResult<int?>(0);
        public Task DeleteSessionTranscriptAsync(Guid sessionId, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<int> DeleteClassroomTranscriptsAsync(Guid classroomId, CancellationToken ct = default)
            => Task.FromResult(0);

        // Deletion never generates a quiz; calling this would mean the code under test took a path
        // it has no business taking, so it fails loudly rather than returning a plausible blank.
        public Task<GeneratedQuizDto> GenerateQuizAsync(
            Guid sessionId, Guid classroomId, int questionCount, int minOptions, int maxOptions,
            IReadOnlyList<string>? avoid = null, bool wholeSession = false,
            CancellationToken ct = default)
            => throw new NotSupportedException("Deletion does not generate quizzes.");

        public Task<GeneratedQuestionDto> GenerateAnswersAsync(
            Guid sessionId, Guid classroomId, string questionText, int minOptions, int maxOptions,
            CancellationToken ct = default)
            => throw new NotSupportedException("Deletion does not generate answers.");
    }

    private sealed class FakeObjectStorage : IRecordingStorage
    {
        public readonly List<string> Deleted = new();
        public List<string>? Timeline;

        public Task DeleteObjectAsync(string objectKey, CancellationToken ct = default)
        {
            Deleted.Add(objectKey);
            Timeline?.Add($"obj:{objectKey}");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFileStorage : IFileStorageService
    {
        public readonly List<string> Deleted = new();

        public Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType, CancellationToken ct = default)
            => Task.FromResult("k");

        public Task DeleteFileAsync(string s3Key, CancellationToken ct = default)
        {
            Deleted.Add(s3Key);
            return Task.CompletedTask;
        }

        public Task<Stream> OpenReadAsync(string s3Key, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream());
    }

    private sealed class FakeKnowledge : IKnowledgeInternalClient
    {
        public int DeIndexCalls;
        public Guid LastDeIndexedClassroomId;
        public bool ThrowOnDeIndex;

        public Task DeIndexClassroomAsync(Guid classroomId, CancellationToken ct = default)
        {
            DeIndexCalls++;
            LastDeIndexedClassroomId = classroomId;
            if (ThrowOnDeIndex)
            {
                throw new InvalidOperationException("de-index failed");
            }
            return Task.CompletedTask;
        }

        // Unused by the deletion service.
        public Task NotifyFileUploadedAsync(Guid fileId, Guid classroomId, string s3Key, string fileName, string contentType, long sizeBytes, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task NotifyFileDeletedAsync(Guid fileId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetIndexingStatusAsync(Guid fileId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<Application.Models.KnowledgeAnswerResult> GetAnswerAsync(Guid classroomId, string question, CancellationToken ct = default)
            => Task.FromResult(new Application.Models.KnowledgeAnswerResult(string.Empty, new List<Application.Models.KnowledgeAnswerSource>()));
        public Task<bool> TriggerSummaryAsync(Guid sessionId, Guid classroomId, CancellationToken ct = default) => Task.FromResult(true);
    }
}
