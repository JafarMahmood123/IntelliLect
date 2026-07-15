using ClassroomService.Application.Abstractions;
using ClassroomService.Application.Exceptions;
using ClassroomService.Application.Services;
using ClassroomService.Infrastructure.Messaging;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
using IntelliLect.Contracts.Messages;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ClassroomService.UnitTests;

/// <summary>
/// Offline e2e for the ClassroomService half of the recording flow (R-1 -> R-4): consume the
/// recording-ready message, store it Available, a member lists it, an authorized member gets a
/// download URL, and a teacher deletes it (object + row). Plus the failure branch: a failed
/// message -> Failed -> download-url returns 409. Everything mocked; no live services.
/// </summary>
public sealed class RecordingFlowE2ETests
{
    private readonly Guid _classroomId = Guid.NewGuid();
    private readonly Guid _teacherId = Guid.NewGuid();
    private readonly Guid _studentId = Guid.NewGuid();
    private readonly Guid _outsiderId = Guid.NewGuid();

    private readonly FakeRecordingRepository _recordings = new();
    private readonly FakeUnitOfWork _uow = new();
    private readonly FakeRecordingMetrics _metrics = new();
    private readonly FakeClassroomRepository _classrooms = new();
    private readonly FakeMembershipRepository _memberships = new();
    private readonly FakeRecordingUrlSigner _signer = new();
    private readonly FakeRecordingStorage _storage = new();

    private ServiceProvider BuildProvider()
    {
        _classrooms.Seed(new Classroom { Id = _classroomId, Name = "C", Description = "", TeacherId = _teacherId });
        _memberships.Enroll(_classroomId, _studentId);

        return new ServiceCollection()
            .AddSingleton<IRecordingRepository>(_recordings)
            .AddSingleton<IUnitOfWork>(_uow)
            .AddSingleton<IRecordingMetrics>(_metrics)
            .AddSingleton<IClassroomRepository>(_classrooms)
            .AddSingleton<IMembershipRepository>(_memberships)
            .AddSingleton<IRecordingUrlSigner>(_signer)
            .AddSingleton<IRecordingStorage>(_storage)
            .AddSingleton<IClock>(new FakeClock())
            .AddSingleton<IRecordingDownloadSettings>(new FakeRecordingDownloadSettings { DownloadUrlTtlSeconds = 600 })
            .AddSingleton<IRecordingLifecycleSettings>(new FakeRecordingLifecycleSettings())
            .AddScoped<IClassroomRecordingService, ClassroomRecordingService>()
            .AddScoped<IRecordingLifecycleService, RecordingLifecycleService>()
            .AddMassTransitTestHarness(x => x.AddConsumer<SessionRecordingReadyConsumer>())
            .BuildServiceProvider(true);
    }

    private async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        Assert.True(condition(), "condition not met within timeout");
    }

    [Fact]
    public async Task Success_path_consume_store_list_download_delete()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var sessionId = Guid.NewGuid();

        // R-1: egress-complete -> recording becomes Available.
        await harness.Bus.Publish(new SessionRecordingReadyMessage(
            sessionId, _classroomId, "recordings/room/f.mp4", 4096, TimeSpan.FromSeconds(30),
            EgressId: "EG_1", ContentType: "video/mp4", Succeeded: true));
        await WaitUntil(() => _recordings.Store.Any(r => r.SessionId == sessionId && r.Status == RecordingStatus.Available));

        var recording = _recordings.Store.Single(r => r.SessionId == sessionId);
        Assert.Equal(1, _metrics.Completed);

        using var scope = provider.CreateScope();
        var recordingService = scope.ServiceProvider.GetRequiredService<IClassroomRecordingService>();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IRecordingLifecycleService>();

        // R-2: an enrolled student lists and sees it Available.
        var listed = await recordingService.ListRecordingsAsync(_classroomId, _studentId, null, null, 1, 10);
        var summary = Assert.Single(listed.Items);
        Assert.Equal("Available", summary.Status);

        // R-3: an authorized member gets a pre-signed download URL.
        var download = await recordingService.GetDownloadUrlAsync(_classroomId, recording.Id, _studentId);
        Assert.Equal(_signer.ReturnUrl, download.Url);
        Assert.Equal(1, _metrics.DownloadIssued);

        // R-4: the teacher deletes it (object + row).
        await lifecycle.DeleteRecordingAsync(_classroomId, recording.Id, _teacherId, isAdmin: false);
        Assert.Contains("recordings/room/f.mp4", _storage.DeletedKeys);
        Assert.DoesNotContain(recording, _recordings.Store);
        Assert.Equal(1, _metrics.Deleted);
    }

    [Fact]
    public async Task Failure_branch_failed_recording_then_download_is_409()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var sessionId = Guid.NewGuid();

        // Egress failed -> recording becomes Failed.
        await harness.Bus.Publish(new SessionRecordingReadyMessage(
            sessionId, _classroomId, S3Key: "", SizeBytes: 0, Duration: TimeSpan.Zero,
            EgressId: "EG_2", ContentType: "video/mp4", Succeeded: false, Error: "encoder crashed"));
        await WaitUntil(() => _recordings.Store.Any(r => r.SessionId == sessionId && r.Status == RecordingStatus.Failed));

        Assert.Equal(1, _metrics.Failed);
        var recording = _recordings.Store.Single(r => r.SessionId == sessionId);

        using var scope = provider.CreateScope();
        var recordingService = scope.ServiceProvider.GetRequiredService<IClassroomRecordingService>();

        // A Failed recording cannot be downloaded -> 409.
        await Assert.ThrowsAsync<ConflictException>(
            () => recordingService.GetDownloadUrlAsync(_classroomId, recording.Id, _studentId));
    }

    [Fact]
    public async Task Non_member_download_is_denied_and_counted()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var sessionId = Guid.NewGuid();
        await harness.Bus.Publish(new SessionRecordingReadyMessage(
            sessionId, _classroomId, "recordings/room/f.mp4", 10, TimeSpan.FromSeconds(5),
            EgressId: "EG_3", ContentType: "video/mp4", Succeeded: true));
        await WaitUntil(() => _recordings.Store.Any(r => r.SessionId == sessionId && r.Status == RecordingStatus.Available));

        var recording = _recordings.Store.Single(r => r.SessionId == sessionId);
        using var scope = provider.CreateScope();
        var recordingService = scope.ServiceProvider.GetRequiredService<IClassroomRecordingService>();

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => recordingService.GetDownloadUrlAsync(_classroomId, recording.Id, _outsiderId));
        Assert.Contains("not_member", _metrics.Denials);
    }
}
