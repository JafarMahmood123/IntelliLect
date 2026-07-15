using ClassroomService.Application.DTOs.Recording;
using ClassroomService.Application.DTOs.Summary;
using ClassroomService.Application.Exceptions;
using ClassroomService.Application.Services;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;

namespace ClassroomService.UnitTests;

public sealed class ClassroomSummaryServiceTests
{
    private readonly Guid _classroomId = Guid.NewGuid();
    private readonly Guid _teacherId = Guid.NewGuid();
    private readonly Guid _studentId = Guid.NewGuid();
    private readonly Guid _outsiderId = Guid.NewGuid();

    private readonly FakeSummaryRepository _summaries = new();
    private readonly FakeClassroomRepository _classrooms = new();
    private readonly FakeMembershipRepository _memberships = new();
    private readonly FakeRecordingUrlSigner _signer = new();
    private readonly FakeSummaryDownloadSettings _settings = new() { DownloadUrlTtlSeconds = 600 };
    private readonly RecordingLogger<ClassroomSummaryService> _logger = new();

    public ClassroomSummaryServiceTests()
    {
        _classrooms.Seed(new Classroom { Id = _classroomId, Name = "C", Description = "", TeacherId = _teacherId });
        _memberships.Enroll(_classroomId, _studentId);
    }

    private ClassroomSummaryService Service()
        => new(_summaries, _classrooms, _memberships, _signer, _settings, _logger);

    private SessionSummary Seed(
        SummaryStatus status = SummaryStatus.Available,
        Guid? classroomId = null,
        Guid? sessionId = null,
        DateTime? createdAt = null)
    {
        var summary = new SessionSummary
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId ?? Guid.NewGuid(),
            ClassroomId = classroomId ?? _classroomId,
            // present but must NEVER surface in the DTO
            MdS3Key = "summaries/secret/object.md",
            PdfS3Key = "summaries/secret/object.pdf",
            Status = status,
            CreatedAtUtc = createdAt ?? DateTime.UtcNow,
            AvailableAtUtc = status == SummaryStatus.Available ? DateTime.UtcNow : null,
        };
        _summaries.Seed(summary);
        return summary;
    }

    // --- Authorization -------------------------------------------------------------------

    [Fact]
    public async Task Teacher_can_list_summaries()
    {
        Seed();
        var result = await Service().ListSummariesAsync(_classroomId, _teacherId, null, 1, 10);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task Enrolled_student_can_list_summaries()
    {
        Seed();
        var result = await Service().ListSummariesAsync(_classroomId, _studentId, null, 1, 10);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task Non_member_listing_is_forbidden()
    {
        Seed();
        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => Service().ListSummariesAsync(_classroomId, _outsiderId, null, 1, 10));
    }

    [Fact]
    public async Task Non_member_get_is_forbidden()
    {
        var summary = Seed();
        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => Service().GetSummaryAsync(_classroomId, summary.Id, _outsiderId));
    }

    // --- Listing shape & ordering --------------------------------------------------------

    [Fact]
    public async Task List_returns_summaries_newest_first()
    {
        var older = Seed(createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = Seed(createdAt: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = await Service().ListSummariesAsync(_classroomId, _teacherId, null, 1, 10);

        Assert.Equal(new[] { newer.Id, older.Id }, result.Items.Select(i => i.SummaryId).ToArray());
    }

    [Fact]
    public async Task Summary_dto_exposes_metadata_but_never_s3key_or_url()
    {
        // Structural guarantee: the DTO type has no s3-key/url-shaped property.
        var propertyNames = typeof(SummarySummaryDto).GetProperties().Select(p => p.Name.ToLowerInvariant());
        Assert.DoesNotContain(propertyNames, n => n.Contains("s3") || n.Contains("key") || n.Contains("url"));

        var summary = Seed();
        var dto = (await Service().ListSummariesAsync(_classroomId, _teacherId, null, 1, 10)).Items.Single();
        Assert.Equal(summary.Id, dto.SummaryId);
        Assert.Equal(summary.SessionId, dto.SessionId);
        Assert.Equal(_classroomId, dto.ClassroomId);
        Assert.Equal("Available", dto.Status);
        Assert.NotNull(dto.AvailableAt);
    }

    [Fact]
    public async Task SessionId_filter_returns_only_that_session()
    {
        var target = Seed();
        Seed(); // different session

        var result = await Service().ListSummariesAsync(_classroomId, _teacherId, target.SessionId, 1, 10);

        Assert.Equal(target.Id, result.Items.Single().SummaryId);
    }

    // --- Get by id -----------------------------------------------------------------------

    [Fact]
    public async Task Get_returns_summary_in_classroom()
    {
        var summary = Seed(SummaryStatus.Generating);

        var dto = await Service().GetSummaryAsync(_classroomId, summary.Id, _studentId);

        Assert.Equal(summary.Id, dto.SummaryId);
        Assert.Equal("Generating", dto.Status);
    }

    [Fact]
    public async Task Get_summary_belonging_to_another_classroom_returns_404()
    {
        var summary = Seed(classroomId: Guid.NewGuid());

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => Service().GetSummaryAsync(_classroomId, summary.Id, _teacherId));
    }

    [Fact]
    public async Task Get_unknown_summary_returns_404()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => Service().GetSummaryAsync(_classroomId, Guid.NewGuid(), _teacherId));
    }

    // --- Download URL --------------------------------------------------------------------

    [Fact]
    public async Task Teacher_gets_download_url_for_available_summary()
    {
        var summary = Seed(SummaryStatus.Available);

        var dto = await Service().GetDownloadUrlAsync(_classroomId, summary.Id, _teacherId, null);

        Assert.Equal(_signer.ReturnUrl, dto.Url);
        Assert.Equal(FakeRecordingUrlSigner.Base + TimeSpan.FromSeconds(600), dto.ExpiresAt);
    }

    [Fact]
    public async Task Enrolled_student_gets_download_url()
    {
        var summary = Seed(SummaryStatus.Available);

        var dto = await Service().GetDownloadUrlAsync(_classroomId, summary.Id, _studentId, "pdf");

        Assert.Equal(_signer.ReturnUrl, dto.Url);
    }

    [Fact]
    public async Task Non_member_download_is_forbidden()
    {
        var summary = Seed(SummaryStatus.Available);

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => Service().GetDownloadUrlAsync(_classroomId, summary.Id, _outsiderId, "pdf"));
        Assert.Equal(0, _signer.Calls); // never mint for a non-member
    }

    [Theory]
    [InlineData(SummaryStatus.Generating)]
    [InlineData(SummaryStatus.Failed)]
    public async Task Non_available_summary_download_is_conflict(SummaryStatus status)
    {
        var summary = Seed(status);

        await Assert.ThrowsAsync<ConflictException>(
            () => Service().GetDownloadUrlAsync(_classroomId, summary.Id, _teacherId, "pdf"));
        Assert.Equal(0, _signer.Calls);
    }

    [Fact]
    public async Task Unknown_summary_download_returns_404()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => Service().GetDownloadUrlAsync(_classroomId, Guid.NewGuid(), _teacherId, "pdf"));
    }

    [Fact]
    public async Task Download_of_summary_in_another_classroom_returns_404()
    {
        var summary = Seed(SummaryStatus.Available, classroomId: Guid.NewGuid());

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => Service().GetDownloadUrlAsync(_classroomId, summary.Id, _teacherId, "pdf"));
    }

    // --- format param --------------------------------------------------------------------

    [Fact]
    public async Task Default_format_presigns_the_pdf_key_with_pdf_disposition()
    {
        var summary = Seed(SummaryStatus.Available);

        await Service().GetDownloadUrlAsync(_classroomId, summary.Id, _teacherId, null);

        Assert.Equal(1, _signer.Calls);
        Assert.Equal(summary.PdfS3Key, _signer.LastKey);           // pdf by default
        Assert.Equal(TimeSpan.FromSeconds(600), _signer.LastTtl);  // TTL == DownloadUrlTtlSeconds
        Assert.StartsWith("attachment;", _signer.LastContentDisposition);
        Assert.Contains($"{summary.SessionId}-summary.pdf", _signer.LastContentDisposition);
        Assert.Equal("application/pdf", _signer.LastContentType);
    }

    [Fact]
    public async Task Format_pdf_presigns_the_pdf_key()
    {
        var summary = Seed(SummaryStatus.Available);

        await Service().GetDownloadUrlAsync(_classroomId, summary.Id, _teacherId, "pdf");

        Assert.Equal(summary.PdfS3Key, _signer.LastKey);
        Assert.Contains(".pdf", _signer.LastContentDisposition);
        Assert.Equal("application/pdf", _signer.LastContentType);
    }

    [Theory]
    [InlineData("md")]
    [InlineData("MD")]
    public async Task Format_md_presigns_the_md_key_with_md_disposition(string format)
    {
        var summary = Seed(SummaryStatus.Available);

        await Service().GetDownloadUrlAsync(_classroomId, summary.Id, _teacherId, format);

        Assert.Equal(summary.MdS3Key, _signer.LastKey);            // markdown key
        Assert.Contains($"{summary.SessionId}-summary.md", _signer.LastContentDisposition);
        Assert.Equal("text/markdown", _signer.LastContentType);
    }

    // --- security ------------------------------------------------------------------------

    [Fact]
    public async Task Url_is_never_written_to_logs()
    {
        var summary = Seed(SummaryStatus.Available);

        var dto = await Service().GetDownloadUrlAsync(_classroomId, summary.Id, _teacherId, "pdf");

        Assert.NotEmpty(_logger.Entries);
        Assert.All(_logger.Entries, e => Assert.DoesNotContain(dto.Url, e.Message));
    }

    [Fact]
    public void DownloadUrlDto_exposes_only_url_and_expiry_never_the_key()
    {
        var propertyNames = typeof(DownloadUrlDto).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToList();
        Assert.DoesNotContain(propertyNames, n => n.Contains("s3") || n.Contains("key"));
        Assert.Equal(new[] { "url", "expiresat" }, propertyNames);
    }
}
