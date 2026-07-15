using ClassroomService.Application.Abstractions;
using ClassroomService.Application.Exceptions;
using ClassroomService.Application.Services;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;

namespace ClassroomService.UnitTests;

public sealed class RecordingLifecycleServiceTests
{
    private readonly Guid _classroomId = Guid.NewGuid();
    private readonly Guid _teacherId = Guid.NewGuid();
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly Guid _studentId = Guid.NewGuid();

    private readonly FakeRecordingRepository _recordings = new();
    private readonly FakeClassroomRepository _classrooms = new();
    private readonly FakeRecordingStorage _storage = new();
    private readonly FakeUnitOfWork _uow = new();
    private readonly FakeClock _clock = new();
    private readonly RecordingLogger<RecordingLifecycleService> _logger = new();

    public RecordingLifecycleServiceTests()
    {
        _classrooms.Seed(new Classroom { Id = _classroomId, Name = "C", Description = "", TeacherId = _teacherId });
    }

    private RecordingLifecycleService Service(
        IRecordingLifecycleSettings? settings = null, FakeRecordingStorage? storage = null)
        => new(_recordings, _classrooms, storage ?? _storage, _uow, _clock,
            settings ?? new FakeRecordingLifecycleSettings(), _logger);

    private SessionRecording Seed(
        RecordingStatus status = RecordingStatus.Available,
        Guid? classroomId = null,
        string? s3Key = "recordings/room/object.mp4",
        DateTime? createdAt = null)
    {
        var rec = new SessionRecording
        {
            Id = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            ClassroomId = classroomId ?? _classroomId,
            EgressId = "EG_x",
            S3Key = s3Key,
            Status = status,
            CreatedAtUtc = createdAt ?? _clock.UtcNow,
        };
        _recordings.Seed(rec);
        return rec;
    }

    // --- Delete: authorization -----------------------------------------------------------

    [Fact]
    public async Task Teacher_can_delete_recording()
    {
        var rec = Seed();

        await Service().DeleteRecordingAsync(_classroomId, rec.Id, _teacherId, isAdmin: false);

        Assert.Contains(rec.S3Key, _storage.DeletedKeys);
        Assert.DoesNotContain(rec, _recordings.Store);
        Assert.True(_uow.SaveChangesCount >= 1);
    }

    [Fact]
    public async Task Admin_can_delete_recording()
    {
        var rec = Seed();

        // Admin who is not the classroom teacher may still delete.
        await Service().DeleteRecordingAsync(_classroomId, rec.Id, _adminId, isAdmin: true);

        Assert.DoesNotContain(rec, _recordings.Store);
    }

    [Fact]
    public async Task Enrolled_student_cannot_delete()
    {
        var rec = Seed();

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => Service().DeleteRecordingAsync(_classroomId, rec.Id, _studentId, isAdmin: false));
        Assert.Contains(rec, _recordings.Store); // untouched
        Assert.Empty(_storage.DeletedKeys);
    }

    [Fact]
    public async Task Non_member_cannot_delete()
    {
        var rec = Seed();

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => Service().DeleteRecordingAsync(_classroomId, rec.Id, Guid.NewGuid(), isAdmin: false));
    }

    // --- Delete: belongs-to-classroom ----------------------------------------------------

    [Fact]
    public async Task Delete_unknown_recording_returns_404()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => Service().DeleteRecordingAsync(_classroomId, Guid.NewGuid(), _teacherId, isAdmin: false));
    }

    [Fact]
    public async Task Delete_recording_in_another_classroom_returns_404()
    {
        var rec = Seed(classroomId: Guid.NewGuid());

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => Service().DeleteRecordingAsync(_classroomId, rec.Id, _teacherId, isAdmin: false));
        Assert.Contains(rec, _recordings.Store);
    }

    // --- Delete: ordering & resilience ---------------------------------------------------

    [Fact]
    public async Task Delete_removes_object_and_row()
    {
        var rec = Seed();

        await Service().DeleteRecordingAsync(_classroomId, rec.Id, _teacherId, isAdmin: false);

        Assert.Single(_storage.DeletedKeys);
        Assert.Empty(_recordings.Store);
    }

    [Fact]
    public async Task Delete_when_s3_delete_fails_keeps_row_and_propagates()
    {
        var rec = Seed();
        var failingStorage = new FakeRecordingStorage(throwOnDelete: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(storage: failingStorage).DeleteRecordingAsync(_classroomId, rec.Id, _teacherId, isAdmin: false));

        // No dangling state: object delete failed, so the row is NOT removed and nothing is saved.
        Assert.Contains(rec, _recordings.Store);
        Assert.Equal(0, _uow.SaveChangesCount);
    }

    [Fact]
    public async Task Delete_recording_without_object_removes_row_without_calling_storage()
    {
        // A Failed recording that never produced an object (S3Key null).
        var rec = Seed(RecordingStatus.Failed, s3Key: null);

        await Service().DeleteRecordingAsync(_classroomId, rec.Id, _teacherId, isAdmin: false);

        Assert.Empty(_storage.DeletedKeys);
        Assert.Empty(_recordings.Store);
    }

    // --- Reconcile -----------------------------------------------------------------------

    [Fact]
    public async Task Reconcile_marks_stuck_processing_as_failed()
    {
        // StuckProcessingMinutes default 30; created 60 minutes ago -> stuck.
        var rec = Seed(RecordingStatus.Processing, createdAt: _clock.UtcNow.AddMinutes(-60));

        var count = await Service().ReconcileStuckProcessingAsync();

        Assert.Equal(1, count);
        Assert.Equal(RecordingStatus.Failed, rec.Status);
        Assert.False(string.IsNullOrEmpty(rec.Error));
    }

    [Fact]
    public async Task Reconcile_leaves_recent_processing_alone()
    {
        var rec = Seed(RecordingStatus.Processing, createdAt: _clock.UtcNow.AddMinutes(-10));

        var count = await Service().ReconcileStuckProcessingAsync();

        Assert.Equal(0, count);
        Assert.Equal(RecordingStatus.Processing, rec.Status);
    }

    [Fact]
    public async Task Reconcile_ignores_non_processing_recordings()
    {
        var rec = Seed(RecordingStatus.Available, createdAt: _clock.UtcNow.AddMinutes(-120));

        var count = await Service().ReconcileStuckProcessingAsync();

        Assert.Equal(0, count);
        Assert.Equal(RecordingStatus.Available, rec.Status);
    }

    [Fact]
    public async Task Reconcile_is_idempotent()
    {
        Seed(RecordingStatus.Processing, createdAt: _clock.UtcNow.AddMinutes(-60));
        var service = Service();

        var first = await service.ReconcileStuckProcessingAsync();
        var second = await service.ReconcileStuckProcessingAsync();

        Assert.Equal(1, first);
        Assert.Equal(0, second); // already Failed -> nothing left to reconcile
    }

    // --- Retention -----------------------------------------------------------------------

    [Fact]
    public async Task Retention_disabled_deletes_nothing()
    {
        Seed(createdAt: _clock.UtcNow.AddDays(-365));

        // Default settings: retention disabled.
        var count = await Service().ApplyRetentionAsync();

        Assert.Equal(0, count);
        Assert.Empty(_storage.DeletedKeys);
        Assert.Single(_recordings.Store);
    }

    [Fact]
    public async Task Retention_enabled_deletes_old_and_keeps_new()
    {
        var settings = new FakeRecordingLifecycleSettings { RetentionEnabled = true, RetentionDays = 30 };
        var old = Seed(createdAt: _clock.UtcNow.AddDays(-60));
        var recent = Seed(createdAt: _clock.UtcNow.AddDays(-10));

        var count = await Service(settings).ApplyRetentionAsync();

        Assert.Equal(1, count);
        Assert.Contains(old.S3Key, _storage.DeletedKeys);
        Assert.DoesNotContain(old, _recordings.Store);
        Assert.Contains(recent, _recordings.Store);
    }

    [Fact]
    public async Task Retention_zero_days_deletes_nothing()
    {
        var settings = new FakeRecordingLifecycleSettings { RetentionEnabled = true, RetentionDays = 0 };
        Seed(createdAt: _clock.UtcNow.AddDays(-365));

        var count = await Service(settings).ApplyRetentionAsync();

        Assert.Equal(0, count);
        Assert.Single(_recordings.Store);
    }
}
