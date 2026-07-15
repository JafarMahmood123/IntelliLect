using ClassroomService.Application.DTOs.Recording;
using ClassroomService.Application.Exceptions;
using ClassroomService.Application.Services;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;

namespace ClassroomService.UnitTests;

public sealed class ClassroomRecordingServiceTests
{
    private readonly Guid _classroomId = Guid.NewGuid();
    private readonly Guid _teacherId = Guid.NewGuid();
    private readonly Guid _studentId = Guid.NewGuid();
    private readonly Guid _outsiderId = Guid.NewGuid();

    private readonly FakeRecordingRepository _recordings = new();
    private readonly FakeClassroomRepository _classrooms = new();
    private readonly FakeMembershipRepository _memberships = new();
    private readonly FakeRecordingUrlSigner _signer = new();
    private readonly FakeRecordingDownloadSettings _settings = new() { DownloadUrlTtlSeconds = 600 };
    private readonly FakeRecordingMetrics _metrics = new();
    private readonly RecordingLogger<ClassroomRecordingService> _logger = new();

    public ClassroomRecordingServiceTests()
    {
        _classrooms.Seed(new Classroom { Id = _classroomId, Name = "C", Description = "", TeacherId = _teacherId });
        _memberships.Enroll(_classroomId, _studentId);
    }

    private ClassroomRecordingService Service()
        => new(_recordings, _classrooms, _memberships, _signer, _settings, _metrics, _logger);

    private SessionRecording Seed(
        RecordingStatus status = RecordingStatus.Available,
        Guid? classroomId = null,
        Guid? sessionId = null,
        DateTime? createdAt = null)
    {
        var rec = new SessionRecording
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId ?? Guid.NewGuid(),
            ClassroomId = classroomId ?? _classroomId,
            EgressId = "EG_" + Guid.NewGuid().ToString("N")[..6],
            S3Key = "recordings/secret/object.mp4", // present but must NEVER surface in the DTO
            Status = status,
            DurationSeconds = 120,
            SizeBytes = 4096,
            ContentType = "video/mp4",
            CreatedAtUtc = createdAt ?? DateTime.UtcNow,
            AvailableAtUtc = status == RecordingStatus.Available ? DateTime.UtcNow : null,
        };
        _recordings.Seed(rec);
        return rec;
    }

    // --- Authorization -------------------------------------------------------------------

    [Fact]
    public async Task Teacher_can_list_recordings()
    {
        Seed();
        var result = await Service().ListRecordingsAsync(_classroomId, _teacherId, null, null, 1, 10);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task Enrolled_student_can_list_recordings()
    {
        Seed();
        var result = await Service().ListRecordingsAsync(_classroomId, _studentId, null, null, 1, 10);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task Non_member_listing_is_forbidden()
    {
        Seed();
        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => Service().ListRecordingsAsync(_classroomId, _outsiderId, null, null, 1, 10));
    }

    [Fact]
    public async Task Non_member_get_is_forbidden()
    {
        var rec = Seed();
        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => Service().GetRecordingAsync(_classroomId, rec.Id, _outsiderId));
    }

    // --- Listing shape & ordering --------------------------------------------------------

    [Fact]
    public async Task List_returns_recordings_newest_first()
    {
        var older = Seed(createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = Seed(createdAt: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = await Service().ListRecordingsAsync(_classroomId, _teacherId, null, null, 1, 10);

        Assert.Equal(new[] { newer.Id, older.Id }, result.Items.Select(i => i.RecordingId).ToArray());
    }

    [Fact]
    public async Task Summary_dto_exposes_metadata_but_never_s3key_or_url()
    {
        // Structural guarantee: the DTO type has no s3-key/url-shaped property.
        var propertyNames = typeof(RecordingSummaryDto).GetProperties().Select(p => p.Name.ToLowerInvariant());
        Assert.DoesNotContain(propertyNames, n => n.Contains("s3") || n.Contains("key") || n.Contains("url"));

        var rec = Seed();
        var dto = (await Service().ListRecordingsAsync(_classroomId, _teacherId, null, null, 1, 10)).Items.Single();
        Assert.Equal(rec.Id, dto.RecordingId);
        Assert.Equal(rec.SessionId, dto.SessionId);
        Assert.Equal(_classroomId, dto.ClassroomId);
        Assert.Equal("Available", dto.Status);
        Assert.Equal(120, dto.DurationSeconds);
        Assert.Equal(4096, dto.SizeBytes);
        Assert.Equal("video/mp4", dto.ContentType);
        Assert.NotNull(dto.AvailableAt);
    }

    // --- Filters -------------------------------------------------------------------------

    [Fact]
    public async Task SessionId_filter_returns_only_that_session()
    {
        var target = Seed();
        Seed(); // different session

        var result = await Service().ListRecordingsAsync(_classroomId, _teacherId, target.SessionId, null, 1, 10);

        Assert.Equal(target.Id, result.Items.Single().RecordingId);
    }

    [Fact]
    public async Task Status_filter_returns_only_matching_status()
    {
        Seed(RecordingStatus.Available);
        Seed(RecordingStatus.Processing);
        Seed(RecordingStatus.Failed);

        var result = await Service().ListRecordingsAsync(_classroomId, _teacherId, null, RecordingStatus.Available, 1, 10);

        Assert.All(result.Items, i => Assert.Equal("Available", i.Status));
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task Default_no_status_filter_includes_processing_and_failed()
    {
        Seed(RecordingStatus.Available);
        Seed(RecordingStatus.Processing);
        Seed(RecordingStatus.Failed);

        var result = await Service().ListRecordingsAsync(_classroomId, _teacherId, null, null, 1, 10);

        var statuses = result.Items.Select(i => i.Status).ToHashSet();
        Assert.Contains("Processing", statuses);
        Assert.Contains("Failed", statuses);
        Assert.Equal(3, result.TotalCount);
    }

    [Fact]
    public async Task Paging_limits_page_size_and_reports_total()
    {
        for (var i = 0; i < 5; i++) Seed(createdAt: new DateTime(2026, 1, 1 + i, 0, 0, 0, DateTimeKind.Utc));

        var result = await Service().ListRecordingsAsync(_classroomId, _teacherId, null, null, 1, 2);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(5, result.TotalCount);
    }

    // --- Get by id -----------------------------------------------------------------------

    [Fact]
    public async Task Get_returns_recording_in_classroom()
    {
        var rec = Seed(RecordingStatus.Processing);

        var dto = await Service().GetRecordingAsync(_classroomId, rec.Id, _studentId);

        Assert.Equal(rec.Id, dto.RecordingId);
        Assert.Equal("Processing", dto.Status);
    }

    [Fact]
    public async Task Get_recording_belonging_to_another_classroom_returns_404()
    {
        // Recording exists but under a different classroom; caller is a member of _classroomId.
        var otherClassroom = Guid.NewGuid();
        var rec = Seed(classroomId: otherClassroom);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => Service().GetRecordingAsync(_classroomId, rec.Id, _teacherId));
    }

    [Fact]
    public async Task Get_unknown_recording_returns_404()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => Service().GetRecordingAsync(_classroomId, Guid.NewGuid(), _teacherId));
    }

    // --- Download URL (R-3) --------------------------------------------------------------

    [Fact]
    public async Task Teacher_gets_download_url_for_available_recording()
    {
        var rec = Seed(RecordingStatus.Available);

        var dto = await Service().GetDownloadUrlAsync(_classroomId, rec.Id, _teacherId);

        Assert.Equal(_signer.ReturnUrl, dto.Url); // passed through unchanged
        Assert.Equal(FakeRecordingUrlSigner.Base + TimeSpan.FromSeconds(600), dto.ExpiresAt);
    }

    [Fact]
    public async Task Enrolled_student_gets_download_url()
    {
        var rec = Seed(RecordingStatus.Available);

        var dto = await Service().GetDownloadUrlAsync(_classroomId, rec.Id, _studentId);

        Assert.Equal(_signer.ReturnUrl, dto.Url);
    }

    [Fact]
    public async Task Non_member_download_is_forbidden()
    {
        var rec = Seed(RecordingStatus.Available);

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => Service().GetDownloadUrlAsync(_classroomId, rec.Id, _outsiderId));
        Assert.Equal(0, _signer.Calls); // never mint for a non-member
    }

    [Theory]
    [InlineData(RecordingStatus.Processing)]
    [InlineData(RecordingStatus.Failed)]
    public async Task Non_available_recording_download_is_conflict(RecordingStatus status)
    {
        var rec = Seed(status);

        await Assert.ThrowsAsync<ConflictException>(
            () => Service().GetDownloadUrlAsync(_classroomId, rec.Id, _teacherId));
        Assert.Equal(0, _signer.Calls);
    }

    [Fact]
    public async Task Unknown_recording_download_returns_404()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => Service().GetDownloadUrlAsync(_classroomId, Guid.NewGuid(), _teacherId));
    }

    [Fact]
    public async Task Download_of_recording_in_another_classroom_returns_404()
    {
        var rec = Seed(RecordingStatus.Available, classroomId: Guid.NewGuid());

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => Service().GetDownloadUrlAsync(_classroomId, rec.Id, _teacherId));
    }

    [Fact]
    public async Task Signer_is_called_with_exact_key_ttl_and_attachment_disposition()
    {
        var rec = Seed(RecordingStatus.Available);

        await Service().GetDownloadUrlAsync(_classroomId, rec.Id, _teacherId);

        Assert.Equal(1, _signer.Calls);
        Assert.Equal(rec.S3Key, _signer.LastKey);                 // exact object key
        Assert.Equal(TimeSpan.FromSeconds(600), _signer.LastTtl); // TTL == DownloadUrlTtlSeconds
        Assert.StartsWith("attachment;", _signer.LastContentDisposition);
        Assert.Contains(".mp4", _signer.LastContentDisposition);
        Assert.Equal("video/mp4", _signer.LastContentType);       // content-type from the recording
    }

    [Fact]
    public async Task Url_is_never_written_to_logs()
    {
        var rec = Seed(RecordingStatus.Available);

        var dto = await Service().GetDownloadUrlAsync(_classroomId, rec.Id, _teacherId);

        // An audit line is expected, but it must NOT contain the bearer URL.
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
