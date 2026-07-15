using ClassroomService.Application.Abstractions;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
using ClassroomService.Infrastructure.Messaging;
using IntelliLect.Contracts.Messages;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ClassroomService.UnitTests;

public sealed class SessionRecordingReadyConsumerTests
{
    private static ServiceProvider BuildProvider(FakeRecordingRepository repo, FakeUnitOfWork uow)
        => new ServiceCollection()
            .AddSingleton<IRecordingRepository>(repo)
            .AddSingleton<IUnitOfWork>(uow)
            .AddMassTransitTestHarness(x => x.AddConsumer<SessionRecordingReadyConsumer>())
            .BuildServiceProvider(true);

    private static async Task WaitForSaves(FakeUnitOfWork uow, int expected)
    {
        // Consumption runs on the harness thread; wait (bounded) until it has committed.
        for (var i = 0; i < 200 && uow.SaveChangesCount < expected; i++)
        {
            await Task.Delay(10);
        }
        Assert.True(uow.SaveChangesCount >= expected, $"expected >= {expected} saves, got {uow.SaveChangesCount}");
    }

    private static SessionRecordingReadyMessage SuccessMessage(Guid sessionId, Guid classroomId) => new(
        sessionId, classroomId, "recordings/room/f.mp4", 555_000, TimeSpan.FromSeconds(42),
        EgressId: "EG_1", ContentType: "video/mp4", Succeeded: true);

    [Fact]
    public async Task Success_message_marks_recording_available_with_metadata()
    {
        var repo = new FakeRecordingRepository();
        var uow = new FakeUnitOfWork();
        await using var provider = BuildProvider(repo, uow);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var sessionId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();
        await harness.Bus.Publish(SuccessMessage(sessionId, classroomId));
        await WaitForSaves(uow, 1);

        var recording = Assert.Single(repo.Store);
        Assert.Equal(RecordingStatus.Available, recording.Status);
        Assert.Equal(sessionId, recording.SessionId);
        Assert.Equal(classroomId, recording.ClassroomId);
        Assert.Equal("recordings/room/f.mp4", recording.S3Key);
        Assert.Equal(555_000, recording.SizeBytes);
        Assert.Equal(42, recording.DurationSeconds);
        Assert.Equal("video/mp4", recording.ContentType);
        Assert.Equal("EG_1", recording.EgressId);
        Assert.NotNull(recording.AvailableAtUtc);
        Assert.Null(recording.Error);
    }

    [Fact]
    public async Task Success_updates_the_existing_processing_row_in_place()
    {
        var sessionId = Guid.NewGuid();
        var repo = new FakeRecordingRepository();
        repo.Seed(new SessionRecording
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            EgressId = "EG_1",
            Status = RecordingStatus.Processing,
            CreatedAtUtc = DateTime.UtcNow,
        });
        var uow = new FakeUnitOfWork();
        await using var provider = BuildProvider(repo, uow);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(SuccessMessage(sessionId, Guid.NewGuid()));
        await WaitForSaves(uow, 1);

        // No new row — the Processing row transitioned to Available.
        var recording = Assert.Single(repo.Store);
        Assert.Equal(RecordingStatus.Available, recording.Status);
    }

    [Fact]
    public async Task Failure_message_marks_recording_failed_with_error()
    {
        var repo = new FakeRecordingRepository();
        var uow = new FakeUnitOfWork();
        await using var provider = BuildProvider(repo, uow);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var sessionId = Guid.NewGuid();
        await harness.Bus.Publish(new SessionRecordingReadyMessage(
            sessionId, Guid.NewGuid(), S3Key: "", SizeBytes: 0, Duration: TimeSpan.Zero,
            EgressId: "EG_1", ContentType: "video/mp4", Succeeded: false, Error: "encoder crashed"));
        await WaitForSaves(uow, 1);

        var recording = Assert.Single(repo.Store);
        Assert.Equal(RecordingStatus.Failed, recording.Status);
        Assert.Equal("encoder crashed", recording.Error);
        Assert.Null(recording.AvailableAtUtc);
    }

    [Fact]
    public async Task Duplicate_delivery_is_idempotent_no_duplicate_rows()
    {
        var repo = new FakeRecordingRepository();
        var uow = new FakeUnitOfWork();
        await using var provider = BuildProvider(repo, uow);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var sessionId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();

        // Redelivery is sequential: the first delivery fully commits before the second arrives
        // (same-message redelivery is also deduped by the outbox inbox in production). The second
        // finds the existing row and updates it in place rather than inserting a new one.
        await harness.Bus.Publish(SuccessMessage(sessionId, classroomId));
        await WaitForSaves(uow, 1);
        await harness.Bus.Publish(SuccessMessage(sessionId, classroomId));
        await WaitForSaves(uow, 2);

        var recording = Assert.Single(repo.Store);
        Assert.Equal(RecordingStatus.Available, recording.Status);
    }
}
