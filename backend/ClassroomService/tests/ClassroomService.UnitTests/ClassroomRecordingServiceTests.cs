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

    public ClassroomRecordingServiceTests()
    {
        _classrooms.Seed(new Classroom { Id = _classroomId, Name = "C", Description = "", TeacherId = _teacherId });
        _memberships.Enroll(_classroomId, _studentId);
    }

    private ClassroomRecordingService Service() => new(_recordings, _classrooms, _memberships);

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
}
