using ClassroomService.Application.Abstractions;
using ClassroomService.Application.Exceptions;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace ClassroomService.Application.Services;

public sealed class RecordingLifecycleService : IRecordingLifecycleService
{
    private readonly IRecordingRepository _recordingRepository;
    private readonly IClassroomRepository _classroomRepository;
    private readonly IRecordingStorage _storage;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IRecordingLifecycleSettings _settings;
    private readonly ILogger<RecordingLifecycleService> _logger;

    public RecordingLifecycleService(
        IRecordingRepository recordingRepository,
        IClassroomRepository classroomRepository,
        IRecordingStorage storage,
        IUnitOfWork unitOfWork,
        IClock clock,
        IRecordingLifecycleSettings settings,
        ILogger<RecordingLifecycleService> logger)
    {
        _recordingRepository = recordingRepository;
        _classroomRepository = classroomRepository;
        _storage = storage;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _settings = settings;
        _logger = logger;
    }

    public async Task DeleteRecordingAsync(
        Guid classroomId,
        Guid recordingId,
        Guid requestingUserId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        await EnsureTeacherOrAdminAsync(classroomId, requestingUserId, isAdmin, ct);

        var recording = await _recordingRepository.GetByIdAsync(recordingId, ct);
        // Unknown, or belongs to another classroom -> 404 (no cross-classroom deletion).
        if (recording is null || recording.ClassroomId != classroomId)
        {
            throw new KeyNotFoundException("Recording not found.");
        }

        await DeleteObjectAndRowAsync(recording, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Recording {RecordingId} in classroom {ClassroomId} deleted by user {UserId}.",
            recordingId, classroomId, requestingUserId);
    }

    public async Task<int> ReconcileStuckProcessingAsync(CancellationToken ct = default)
    {
        var cutoff = _clock.UtcNow.AddMinutes(-Math.Max(1, _settings.StuckProcessingMinutes));
        var stuck = await _recordingRepository.GetStuckProcessingAsync(cutoff, ct);
        if (stuck.Count == 0)
        {
            return 0;
        }

        foreach (var recording in stuck)
        {
            // Conservative: without an authoritative egress result we mark Failed rather than guess
            // Available. Actively querying LiveKit/StreamingService egress status by egress_id is a
            // future improvement.
            recording.Status = RecordingStatus.Failed;
            recording.Error = $"Reconcile timeout: no egress-complete within {_settings.StuckProcessingMinutes} minutes.";
        }

        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation("Reconciled {Count} stuck Processing recording(s) to Failed.", stuck.Count);
        return stuck.Count;
    }

    public async Task<int> ApplyRetentionAsync(CancellationToken ct = default)
    {
        if (!_settings.RetentionEnabled || _settings.RetentionDays <= 0)
        {
            return 0; // retention off -> nothing is auto-deleted
        }

        var cutoff = _clock.UtcNow.AddDays(-_settings.RetentionDays);
        var expired = await _recordingRepository.GetOlderThanAsync(cutoff, ct);

        var deleted = 0;
        foreach (var recording in expired)
        {
            try
            {
                await DeleteObjectAndRowAsync(recording, ct);
                deleted++;
            }
            catch (Exception ex)
            {
                // Per-item resilience: a failed object delete leaves that row for the next pass.
                _logger.LogWarning(
                    ex,
                    "Retention: failed to delete recording {RecordingId}; leaving it for a later pass.",
                    recording.Id);
            }
        }

        if (deleted > 0)
        {
            await _unitOfWork.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Retention deleted {Count} recording(s) older than {RetentionDays} day(s).",
                deleted, _settings.RetentionDays);
        }

        return deleted;
    }

    /// <summary>
    /// Deletes the S3 object first, then marks the row for removal. S3-first + idempotent DELETE
    /// means we never leave a row pointing at a deleted object; if the object delete throws, this
    /// propagates BEFORE the row is touched, so state stays recoverable. Does not save.
    /// </summary>
    private async Task DeleteObjectAndRowAsync(SessionRecording recording, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(recording.S3Key))
        {
            try
            {
                await _storage.DeleteObjectAsync(recording.S3Key, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to delete S3 object for recording {RecordingId}; keeping the metadata row for retry.",
                    recording.Id);
                throw;
            }
        }

        await _recordingRepository.RemoveAsync(recording, ct);
    }

    /// <summary>
    /// Delete authorization: only the classroom's teacher or an admin (NOT enrolled students).
    /// Admins bypass the classroom lookup; otherwise missing classroom -> 404, non-teacher -> 403.
    /// </summary>
    private async Task EnsureTeacherOrAdminAsync(Guid classroomId, Guid userId, bool isAdmin, CancellationToken ct)
    {
        if (isAdmin)
        {
            return;
        }

        var classroom = await _classroomRepository.GetByIdAsync(classroomId, ct)
            ?? throw new KeyNotFoundException("Classroom not found.");

        if (classroom.TeacherId != userId)
        {
            throw new ForbiddenAccessException("Only the classroom's teacher or an admin may delete recordings.");
        }
    }
}
