using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;
using UserManagementService.Application.DTOs.Output;
using UserManagementService.Application.OutputAdministration;

namespace UserManagementService.UnitTests.OutputAdministration;

// Unit tests for OutputAdminService — the super-admin recordings/summaries gateway.
//   List -> proxied from ClassroomService. Delete -> 4أ (reason required), delegates per type,
//   propagates NotFound (5أ) / InvalidOperation (5ب).
public class OutputAdminServiceTests
{
    [Fact]
    public async Task GetOutputs_ProxiesAndMaps()
    {
        var client = new FakeOutputClassroomClient
        {
            Page = new AdminOutputPage(
                new[]
                {
                    new AdminOutput(Guid.NewGuid(), "Recording", Guid.NewGuid(), "Week 1",
                        Guid.NewGuid(), "Physics", "Available", 2048, DateTime.UtcNow),
                }, 1, 1, 20),
        };
        var sut = new OutputAdminService(client);

        var result = await sut.GetOutputsAsync(new SearchOutputsRequest { Page = 1, PageSize = 20 });

        var item = Assert.Single(result.Items);
        Assert.Equal("Recording", item.Type);
        Assert.Equal("Week 1", item.SessionTitle);
        Assert.Equal("Physics", item.ClassName);
        Assert.Equal(1, result.TotalCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DeleteRecording_WithoutReason_ThrowsAndDoesNotCallClient(string reason)
    {
        var client = new FakeOutputClassroomClient();
        var sut = new OutputAdminService(client);

        await Assert.ThrowsAsync<ArgumentException>(() => sut.DeleteRecordingAsync(Guid.NewGuid(), reason)); // 4أ
        Assert.False(client.RecordingDeleteCalled);
    }

    [Fact]
    public async Task DeleteRecording_TrimsReasonAndDelegates()
    {
        var id = Guid.NewGuid();
        var client = new FakeOutputClassroomClient
        {
            RecordingResult = new OutputDeletionResult(id, "Recording", true, true),
        };
        var sut = new OutputAdminService(client);

        var result = await sut.DeleteRecordingAsync(id, "  cleanup  ");

        Assert.True(client.RecordingDeleteCalled);
        Assert.Equal(id, client.LastRecordingId);
        Assert.Equal("cleanup", client.LastReason); // trimmed
        Assert.True(result.StorageDeleted);
        Assert.Equal("Recording", result.Type);
    }

    [Fact]
    public async Task DeleteSummary_Delegates()
    {
        var id = Guid.NewGuid();
        var client = new FakeOutputClassroomClient
        {
            SummaryResult = new OutputDeletionResult(id, "Summary", true, true),
        };
        var sut = new OutputAdminService(client);

        var result = await sut.DeleteSummaryAsync(id, "cleanup");

        Assert.True(client.SummaryDeleteCalled);
        Assert.Equal("Summary", result.Type);
    }

    [Fact]
    public async Task DeleteRecording_WhenNotFound_Propagates()
    {
        var client = new FakeOutputClassroomClient { RecordingThrows = new NotFoundException("Output not found.") };
        var sut = new OutputAdminService(client);

        await Assert.ThrowsAsync<NotFoundException>(() => sut.DeleteRecordingAsync(Guid.NewGuid(), "cleanup")); // 5أ
    }

    [Fact]
    public async Task DeleteRecording_WhenSessionLive_PropagatesInvalidOperation()
    {
        var client = new FakeOutputClassroomClient { RecordingThrows = new InvalidOperationException("live") };
        var sut = new OutputAdminService(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.DeleteRecordingAsync(Guid.NewGuid(), "cleanup")); // 5ب
    }
}

// ----- fake -------------------------------------------------------------------

internal sealed class FakeOutputClassroomClient : IClassroomInternalClient
{
    public AdminOutputPage Page { get; set; } = new(Array.Empty<AdminOutput>(), 0, 1, 20);
    public OutputDeletionResult RecordingResult { get; set; } = new(Guid.NewGuid(), "Recording", true, true);
    public OutputDeletionResult SummaryResult { get; set; } = new(Guid.NewGuid(), "Summary", true, true);
    public Exception? RecordingThrows { get; set; }
    public bool RecordingDeleteCalled { get; private set; }
    public bool SummaryDeleteCalled { get; private set; }
    public Guid LastRecordingId { get; private set; }
    public string? LastReason { get; private set; }

    public Task<AdminOutputPage> GetOutputsAsync(int page, int pageSize, string? search, string? type, string? status, Guid? classroomId, CancellationToken ct = default)
        => Task.FromResult(Page);

    public Task<OutputDeletionResult> DeleteRecordingAsync(Guid recordingId, string reason, CancellationToken ct = default)
    {
        RecordingDeleteCalled = true;
        LastRecordingId = recordingId;
        LastReason = reason;
        return RecordingThrows is not null
            ? Task.FromException<OutputDeletionResult>(RecordingThrows)
            : Task.FromResult(RecordingResult);
    }

    public Task<OutputDeletionResult> DeleteSummaryAsync(Guid summaryId, string reason, CancellationToken ct = default)
    {
        SummaryDeleteCalled = true;
        LastReason = reason;
        return Task.FromResult(SummaryResult);
    }

    // Unused.
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
    public Task<AdminFilePage> GetFilesAsync(int page, int pageSize, string? search, Guid? classroomId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<AdminFile>> GetFilesByIdsAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<ClassroomName>> GetClassroomNamesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<FileDeletionResult> DeleteFileAsync(Guid fileId, string reason, CancellationToken ct = default) => throw new NotImplementedException();
}
