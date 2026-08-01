using ClassroomService.Application.Abstractions;
using ClassroomService.Application.Exceptions;
using ClassroomService.Application.Services;
using ClassroomService.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassroomService.UnitTests;

// Unit tests for SessionDeletionService — the super-admin "حذف سجل جلسة مع مخرجاتها" orchestration.
//   4أ -> missing reason. 5أ -> session missing. 5ب -> a live session blocks deletion.
//   Step 6 -> mark PendingDeletion, then purge recording -> summary -> transcript -> session row,
//             object-before-row.
//   6أ -> a missing output is skipped. 6ب -> a step fails (here transcript delete): the session
//         stays PendingDeletion and is NOT removed, so a re-run resumes.
public class SessionDeletionServiceTests
{
    [Fact]
    public async Task Delete_WithoutReason_ThrowsArgumentException()
    {
        var repo = new FakeRepo(EndedSession());
        var sut = Build(repo, new FakeObjectStorage(), new FakeLiveAssistant());

        await Assert.ThrowsAsync<ArgumentException>(() => sut.DeleteAsync(repo.Session!.Id, "  "));
        Assert.Equal(SessionStatus.Ended, repo.Session!.Status);
    }

    [Fact]
    public async Task Delete_WhenMissing_ThrowsKeyNotFound()
    {
        var repo = new FakeRepo(null);
        var sut = Build(repo, new FakeObjectStorage(), new FakeLiveAssistant());

        await Assert.ThrowsAsync<KeyNotFoundException>(() => sut.DeleteAsync(Guid.NewGuid(), "done"));
    }

    [Fact]
    public async Task Delete_WhenLive_ThrowsConflictAndDoesNotMarkPending()
    {
        var session = EndedSession();
        session.Status = SessionStatus.Live;
        var repo = new FakeRepo(session);
        var sut = Build(repo, new FakeObjectStorage(), new FakeLiveAssistant());

        await Assert.ThrowsAsync<ConflictException>(() => sut.DeleteAsync(session.Id, "done"));
        Assert.Equal(SessionStatus.Live, session.Status);
        Assert.False(repo.SessionRemoved);
    }

    [Fact]
    public async Task Delete_HappyPath_PurgesOutputsAndRemovesSession()
    {
        var session = EndedSession();
        var repo = new FakeRepo(session)
        {
            Recording = new SessionRecording { Id = Guid.NewGuid(), SessionId = session.Id, S3Key = "rec/1.mp4" },
            Summary = new SessionSummary { Id = Guid.NewGuid(), SessionId = session.Id, MdS3Key = "sum/1.md", PdfS3Key = "sum/1.pdf" },
        };
        var storage = new FakeObjectStorage();
        var la = new FakeLiveAssistant();
        var sut = Build(repo, storage, la);

        var result = await sut.DeleteAsync(session.Id, "duplicate");

        Assert.Contains("rec/1.mp4", storage.Deleted);
        Assert.Contains("sum/1.md", storage.Deleted);
        Assert.Contains("sum/1.pdf", storage.Deleted);
        Assert.Equal(session.Id, la.LastDeletedSessionId);
        Assert.True(repo.SessionRemoved);

        Assert.True(result.RecordingDeleted);
        Assert.True(result.SummaryDeleted);
        Assert.True(result.TranscriptDeleted);
    }

    [Fact]
    public async Task Delete_DeletesObjectBeforeRow()
    {
        var session = EndedSession();
        var timeline = new List<string>();
        var repo = new FakeRepo(session)
        {
            Recording = new SessionRecording { Id = Guid.NewGuid(), SessionId = session.Id, S3Key = "rec/1.mp4" },
            Timeline = timeline,
        };
        var storage = new FakeObjectStorage { Timeline = timeline };
        var sut = Build(repo, storage, new FakeLiveAssistant());

        await sut.DeleteAsync(session.Id, "done");

        var obj = timeline.IndexOf("obj:rec/1.mp4");
        var row = timeline.IndexOf("recording-removed");
        Assert.True(obj >= 0 && row >= 0 && obj < row);
    }

    [Fact]
    public async Task Delete_WithNoOutputs_SkipsAndStillDeletesSession()
    {
        // Alternate path 6أ: the session ended without recording/summary/transcript.
        var session = EndedSession();
        var repo = new FakeRepo(session); // no recording/summary
        var storage = new FakeObjectStorage();
        var sut = Build(repo, storage, new FakeLiveAssistant());

        var result = await sut.DeleteAsync(session.Id, "done");

        Assert.Empty(storage.Deleted);
        Assert.False(result.RecordingDeleted);
        Assert.False(result.SummaryDeleted);
        Assert.True(repo.SessionRemoved);
    }

    [Fact]
    public async Task Delete_WhenTranscriptDeleteFails_LeavesSessionPendingDeletion()
    {
        // Alternate path 6ب: the transcript step throws; the deletion must halt.
        var session = EndedSession();
        var repo = new FakeRepo(session);
        var la = new FakeLiveAssistant { ThrowOnDelete = true };
        var sut = Build(repo, new FakeObjectStorage(), la);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.DeleteAsync(session.Id, "done"));

        Assert.Equal(SessionStatus.PendingDeletion, session.Status);
        Assert.False(repo.SessionRemoved); // session row survives for a resumable re-run
    }

    [Fact]
    public async Task Delete_OnResume_Completes()
    {
        var session = EndedSession();
        session.Status = SessionStatus.PendingDeletion; // a previous run already marked it
        var repo = new FakeRepo(session);
        var sut = Build(repo, new FakeObjectStorage(), new FakeLiveAssistant());

        var result = await sut.DeleteAsync(session.Id, "resume");

        Assert.True(repo.SessionRemoved);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetImpact_WhenMissing_ReturnsNull()
    {
        var repo = new FakeRepo(null);
        var sut = Build(repo, new FakeObjectStorage(), new FakeLiveAssistant());

        Assert.Null(await sut.GetImpactAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetImpact_ReportsOutputsAndStorage()
    {
        var session = EndedSession();
        var repo = new FakeRepo(session)
        {
            Recording = new SessionRecording { Id = Guid.NewGuid(), SessionId = session.Id, S3Key = "rec", SizeBytes = 2048 },
            Summary = new SessionSummary { Id = Guid.NewGuid(), SessionId = session.Id },
        };
        var la = new FakeLiveAssistant { SegmentCount = 5 };
        var sut = Build(repo, new FakeObjectStorage(), la);

        var impact = await sut.GetImpactAsync(session.Id);

        Assert.NotNull(impact);
        Assert.True(impact!.HasRecording);
        Assert.True(impact.HasSummary);
        Assert.True(impact.HasTranscript);
        Assert.Equal(2048, impact.StorageBytes);
        Assert.False(impact.IsLive);
        Assert.False(impact.TranscriptUnavailable);
    }

    [Fact]
    public async Task GetImpact_WhenTranscriptCheckFails_MarksUnavailable()
    {
        var session = EndedSession();
        var repo = new FakeRepo(session);
        var la = new FakeLiveAssistant { ThrowOnGet = true };
        var sut = Build(repo, new FakeObjectStorage(), la);

        var impact = await sut.GetImpactAsync(session.Id);

        Assert.NotNull(impact);
        Assert.True(impact!.TranscriptUnavailable);
        Assert.False(impact.HasTranscript);
    }

    // --- helpers ---------------------------------------------------------------

    private static Session EndedSession() => new()
    {
        Id = Guid.NewGuid(),
        ClassroomId = Guid.NewGuid(),
        Title = "Week 1",
        Status = SessionStatus.Ended,
    };

    private static SessionDeletionService Build(FakeRepo repo, FakeObjectStorage storage, FakeLiveAssistant la)
        => new(repo, storage, la, NullLogger<SessionDeletionService>.Instance);

    private sealed class FakeRepo : ISessionDeletionRepository
    {
        public Session? Session;
        public SessionRecording? Recording;
        public SessionSummary? Summary;
        public bool SessionRemoved;
        public List<string>? Timeline;

        public FakeRepo(Session? session) => Session = session;

        public Task<Session?> GetTrackedAsync(Guid sessionId, CancellationToken ct = default)
            => Task.FromResult(Session is not null && Session.Id == sessionId ? Session : null);

        public Task<SessionRecording?> GetRecordingAsync(Guid sessionId, CancellationToken ct = default)
            => Task.FromResult(Recording);

        public Task<SessionSummary?> GetSummaryAsync(Guid sessionId, CancellationToken ct = default)
            => Task.FromResult(Summary);

        public void RemoveRecording(SessionRecording recording)
        {
            Recording = null;
            Timeline?.Add("recording-removed");
        }

        public void RemoveSummary(SessionSummary summary) => Summary = null;
        public void RemoveSession(Session session) => SessionRemoved = true;
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
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

    private sealed class FakeLiveAssistant : ILiveAssistantInternalClient
    {
        public int SegmentCount;
        public bool ThrowOnGet;
        public bool ThrowOnDelete;
        public Guid LastDeletedSessionId;

        public Task<int?> GetTranscriptSegmentCountAsync(Guid sessionId, CancellationToken ct = default)
        {
            if (ThrowOnGet) throw new InvalidOperationException("unavailable");
            return Task.FromResult<int?>(SegmentCount);
        }

        public Task DeleteSessionTranscriptAsync(Guid sessionId, CancellationToken ct = default)
        {
            if (ThrowOnDelete) throw new InvalidOperationException("transcript delete failed");
            LastDeletedSessionId = sessionId;
            return Task.CompletedTask;
        }

        public Task<int> DeleteClassroomTranscriptsAsync(Guid classroomId, CancellationToken ct = default)
            => Task.FromResult(0);

        // Deletion never generates a quiz; calling this would mean the code under test took a path
        // it has no business taking, so it fails loudly rather than returning a plausible blank.
        public Task<GeneratedQuizDto> GenerateQuizAsync(
            Guid sessionId, Guid classroomId, int questionCount, int minOptions, int maxOptions,
            IReadOnlyList<string>? avoid = null, CancellationToken ct = default)
            => throw new NotSupportedException("Deletion does not generate quizzes.");

        public Task<GeneratedQuestionDto> GenerateAnswersAsync(
            Guid sessionId, Guid classroomId, string questionText, int minOptions, int maxOptions,
            CancellationToken ct = default)
            => throw new NotSupportedException("Deletion does not generate answers.");
    }
}
