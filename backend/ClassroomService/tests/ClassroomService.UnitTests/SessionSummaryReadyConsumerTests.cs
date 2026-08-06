using ClassroomService.Application.Abstractions;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
using ClassroomService.Infrastructure.Messaging;
using IntelliLect.Contracts.Messages;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ClassroomService.UnitTests;

public sealed class SessionSummaryReadyConsumerTests
{
    private static ServiceProvider BuildProvider(
        FakeSummaryRepository repo, FakeUnitOfWork uow, FakeSummaryMetrics? metrics = null)
        => new ServiceCollection()
            .AddSingleton<ISummaryRepository>(repo)
            .AddSingleton<IUnitOfWork>(uow)
            .AddSingleton<ISummaryMetrics>(metrics ?? new FakeSummaryMetrics())
            .AddMassTransitTestHarness(x => x.AddConsumer<SessionSummaryReadyConsumer>())
            .BuildServiceProvider(true);

    private static async Task WaitForSaves(FakeUnitOfWork uow, int expected)
    {
        for (var i = 0; i < 200 && uow.SaveChangesCount < expected; i++)
        {
            await Task.Delay(10);
        }
        Assert.True(uow.SaveChangesCount >= expected, $"expected >= {expected} saves, got {uow.SaveChangesCount}");
    }

    private static SessionSummaryReadyMessage SuccessMessage(Guid sessionId, Guid classroomId) => new(
        sessionId, classroomId,
        MdS3Key: "summaries/classroom/s.md",
        PdfS3Key: "summaries/classroom/s.pdf",
        GeneratedAt: DateTimeOffset.UtcNow,
        Succeeded: true);

    [Fact]
    public async Task Success_message_marks_summary_available_with_both_keys()
    {
        var repo = new FakeSummaryRepository();
        var uow = new FakeUnitOfWork();
        var metrics = new FakeSummaryMetrics();
        await using var provider = BuildProvider(repo, uow, metrics);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var sessionId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();
        await harness.Bus.Publish(SuccessMessage(sessionId, classroomId));
        await WaitForSaves(uow, 1);

        var summary = Assert.Single(repo.Store);
        Assert.Equal(SummaryStatus.Available, summary.Status);
        Assert.Equal(sessionId, summary.SessionId);
        Assert.Equal(classroomId, summary.ClassroomId);
        Assert.Equal("summaries/classroom/s.md", summary.MdS3Key);
        Assert.Equal("summaries/classroom/s.pdf", summary.PdfS3Key);
        Assert.NotNull(summary.AvailableAtUtc);
        Assert.Null(summary.Error);
        Assert.True(metrics.AvailableIncrements >= 1);
    }

    [Fact]
    public async Task Success_updates_the_existing_generating_row_in_place()
    {
        var sessionId = Guid.NewGuid();
        var repo = new FakeSummaryRepository();
        repo.Seed(new SessionSummary
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Status = SummaryStatus.Generating,
            CreatedAtUtc = DateTime.UtcNow,
        });
        var uow = new FakeUnitOfWork();
        await using var provider = BuildProvider(repo, uow);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(SuccessMessage(sessionId, Guid.NewGuid()));
        await WaitForSaves(uow, 1);

        // No new row — the Generating row transitioned to Available.
        var summary = Assert.Single(repo.Store);
        Assert.Equal(SummaryStatus.Available, summary.Status);
    }

    [Fact]
    public async Task Failure_message_marks_summary_failed_with_error()
    {
        var repo = new FakeSummaryRepository();
        var uow = new FakeUnitOfWork();
        await using var provider = BuildProvider(repo, uow);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var sessionId = Guid.NewGuid();
        await harness.Bus.Publish(new SessionSummaryReadyMessage(
            sessionId, Guid.NewGuid(), MdS3Key: "", PdfS3Key: "",
            GeneratedAt: DateTimeOffset.UtcNow, Succeeded: false, Error: "ollama down"));
        await WaitForSaves(uow, 1);

        var summary = Assert.Single(repo.Store);
        Assert.Equal(SummaryStatus.Failed, summary.Status);
        Assert.Equal("ollama down", summary.Error);
        Assert.Null(summary.AvailableAtUtc);
    }

    [Fact]
    public async Task Duplicate_delivery_is_idempotent_no_duplicate_rows()
    {
        var repo = new FakeSummaryRepository();
        var uow = new FakeUnitOfWork();
        await using var provider = BuildProvider(repo, uow);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var sessionId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();

        await harness.Bus.Publish(SuccessMessage(sessionId, classroomId));
        await WaitForSaves(uow, 1);
        await harness.Bus.Publish(SuccessMessage(sessionId, classroomId));
        await WaitForSaves(uow, 2);

        var summary = Assert.Single(repo.Store);
        Assert.Equal(SummaryStatus.Available, summary.Status);
    }

    [Fact]
    public async Task A_database_failure_faults_the_message_rather_than_swallowing_it()
    {
        // L-04. The summary itself already succeeded in RagService by the time this arrives, so
        // swallowing a local write failure leaves the classroom showing "Generating" forever for
        // a summary that is finished and sitting in S3 — and the retry that would have fixed it
        // never runs, because the broker was told the message was handled.
        var repo = new FakeSummaryRepository();
        var uow = new FakeUnitOfWork
        {
            BeforeSave = () => throw new InvalidOperationException("the database went away"),
        };
        await using var provider = BuildProvider(repo, uow);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(SuccessMessage(Guid.NewGuid(), Guid.NewGuid()));

        Assert.True(await harness.Published.Any<Fault<SessionSummaryReadyMessage>>());
    }
}
