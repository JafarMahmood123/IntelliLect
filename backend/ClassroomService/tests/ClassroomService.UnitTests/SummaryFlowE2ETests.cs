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
/// Offline e2e for the ClassroomService half of the summary flow (S-4/S-5): consume the
/// summary-ready message, store it Available (bumping the available gauge), a member lists it, and
/// an authorized member gets a pre-signed download URL for both the PDF and Markdown artifacts.
/// Plus the failure branch: a failed message -> Failed -> download-url returns 409; and a
/// non-member is denied and counted. Everything mocked; no live services.
/// </summary>
public sealed class SummaryFlowE2ETests
{
    private readonly Guid _classroomId = Guid.NewGuid();
    private readonly Guid _teacherId = Guid.NewGuid();
    private readonly Guid _studentId = Guid.NewGuid();
    private readonly Guid _outsiderId = Guid.NewGuid();

    private readonly FakeSummaryRepository _summaries = new();
    private readonly FakeUnitOfWork _uow = new();
    private readonly RecordingEventBus _eventBus = new();
    private readonly FakeSummaryMetrics _metrics = new();
    private readonly FakeClassroomRepository _classrooms = new();
    private readonly FakeMembershipRepository _memberships = new();
    private readonly FakeRecordingUrlSigner _signer = new();

    private ServiceProvider BuildProvider()
    {
        _classrooms.Seed(new Classroom { Id = _classroomId, Name = "C", Description = "", TeacherId = _teacherId });
        _memberships.Enroll(_classroomId, _studentId);

        return new ServiceCollection()
            .AddSingleton<ISummaryRepository>(_summaries)
            .AddSingleton<IUnitOfWork>(_uow)
            .AddSingleton<ISummaryMetrics>(_metrics)
            .AddSingleton<IClassroomRepository>(_classrooms)
            .AddSingleton<IMembershipRepository>(_memberships)
            .AddSingleton<IRecordingUrlSigner>(_signer)
            .AddSingleton<ISummaryDownloadSettings>(new FakeSummaryDownloadSettings { DownloadUrlTtlSeconds = 600 })
            .AddSingleton<IEventBus>(_eventBus)
            .AddScoped<IClassroomSummaryService, ClassroomSummaryService>()
            .AddMassTransitTestHarness(x => x.AddConsumer<SessionSummaryReadyConsumer>())
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
    public async Task Success_path_consume_store_list_download_pdf_and_md()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var sessionId = Guid.NewGuid();

        // S-4: summary pipeline finished -> summary becomes Available (and the gauge bumps).
        await harness.Bus.Publish(new SessionSummaryReadyMessage(
            sessionId, _classroomId,
            MdS3Key: "summaries/c/s.md",
            PdfS3Key: "summaries/c/s.pdf",
            GeneratedAt: DateTimeOffset.UtcNow,
            Succeeded: true));
        await WaitUntil(() =>
            _summaries.Store.Any(s => s.SessionId == sessionId && s.Status == SummaryStatus.Available)
            && _metrics.AvailableIncrements >= 1);

        var summary = _summaries.Store.Single(s => s.SessionId == sessionId);

        using var scope = provider.CreateScope();
        var summaryService = scope.ServiceProvider.GetRequiredService<IClassroomSummaryService>();

        // An enrolled student lists and sees it Available.
        var listed = await summaryService.ListSummariesAsync(_classroomId, _studentId, null, 1, 10);
        var dto = Assert.Single(listed.Items);
        Assert.Equal("Available", dto.Status);

        // A PDF download URL: presigns the pdf key and counts the "pdf" format.
        var pdf = await summaryService.GetDownloadUrlAsync(_classroomId, summary.Id, _studentId, "pdf");
        Assert.Equal(_signer.ReturnUrl, pdf.Url);
        Assert.Equal("summaries/c/s.pdf", _signer.LastKey);
        Assert.Contains("pdf", _metrics.IssuedFormats);

        // An MD download URL: presigns the md key and counts the "md" format.
        var md = await summaryService.GetDownloadUrlAsync(_classroomId, summary.Id, _studentId, "md");
        Assert.Equal(_signer.ReturnUrl, md.Url);
        Assert.Equal("summaries/c/s.md", _signer.LastKey);
        Assert.Contains("md", _metrics.IssuedFormats);
    }

    [Fact]
    public async Task Failure_branch_failed_summary_then_download_is_409()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var sessionId = Guid.NewGuid();

        // Pipeline failed -> summary becomes Failed.
        await harness.Bus.Publish(new SessionSummaryReadyMessage(
            sessionId, _classroomId, MdS3Key: "", PdfS3Key: "",
            GeneratedAt: DateTimeOffset.UtcNow, Succeeded: false, Error: "ollama down"));
        await WaitUntil(() => _summaries.Store.Any(s => s.SessionId == sessionId && s.Status == SummaryStatus.Failed));

        var summary = _summaries.Store.Single(s => s.SessionId == sessionId);

        using var scope = provider.CreateScope();
        var summaryService = scope.ServiceProvider.GetRequiredService<IClassroomSummaryService>();

        // A Failed summary cannot be downloaded -> 409.
        await Assert.ThrowsAsync<ConflictException>(
            () => summaryService.GetDownloadUrlAsync(_classroomId, summary.Id, _teacherId, "pdf"));
    }

    [Fact]
    public async Task Non_member_download_is_denied_and_counted()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var sessionId = Guid.NewGuid();
        await harness.Bus.Publish(new SessionSummaryReadyMessage(
            sessionId, _classroomId,
            MdS3Key: "summaries/c/s.md",
            PdfS3Key: "summaries/c/s.pdf",
            GeneratedAt: DateTimeOffset.UtcNow,
            Succeeded: true));
        await WaitUntil(() => _summaries.Store.Any(s => s.SessionId == sessionId && s.Status == SummaryStatus.Available));

        var summary = _summaries.Store.Single(s => s.SessionId == sessionId);
        using var scope = provider.CreateScope();
        var summaryService = scope.ServiceProvider.GetRequiredService<IClassroomSummaryService>();

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => summaryService.GetDownloadUrlAsync(_classroomId, summary.Id, _outsiderId, "pdf"));
        Assert.Contains("not_member", _metrics.Denials);
    }
}
