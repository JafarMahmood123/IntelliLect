using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Output;
using ClassroomService.Application.Exceptions;
using ClassroomService.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace ClassroomService.Application.Services;

public sealed class OutputAdminService : IOutputAdminService
{
    private const string RecordingType = "Recording";
    private const string SummaryType = "Summary";

    private readonly IOutputAdminRepository _repository;
    private readonly IRecordingStorage _objectStorage;
    private readonly ILogger<OutputAdminService> _logger;

    public OutputAdminService(
        IOutputAdminRepository repository,
        IRecordingStorage objectStorage,
        ILogger<OutputAdminService> logger)
    {
        _repository = repository;
        _objectStorage = objectStorage;
        _logger = logger;
    }

    public async Task<AdminOutputPage> GetOutputsAsync(
        string? search, string? type, string? status, Guid? classroomId, int page, int pageSize, CancellationToken ct = default)
    {
        var normalizedPage = page < 1 ? 1 : page;
        var normalizedPageSize = Math.Clamp(pageSize < 1 ? 20 : pageSize, 1, 100);

        var (items, total) = await _repository.GetOutputsPagedAsync(
            search, type, status, classroomId, normalizedPage, normalizedPageSize, ct);
        return new AdminOutputPage(items, total, normalizedPage, normalizedPageSize);
    }

    public async Task<OutputDeletionResult> DeleteRecordingAsync(Guid recordingId, string reason, CancellationToken ct = default)
    {
        RequireReason(reason);

        // 5أ: the recording must exist.
        var recording = await _repository.GetRecordingAsync(recordingId, ct)
            ?? throw new KeyNotFoundException("Recording not found.");

        // 5ب: its session must not be live (the recording may still be being written).
        await EnsureSessionNotLiveAsync(recording.SessionId, ct);

        // Step 6: mark PendingDeletion first, then remove the object, then the row. The single S3 key
        // (null until the recording is Available) is deleted object-before-row.
        if (recording.Status != RecordingStatus.PendingDeletion)
        {
            recording.Status = RecordingStatus.PendingDeletion;
            await _repository.SaveChangesAsync(ct);
        }

        await DeleteObjectAsync(recording.S3Key, ct);

        _repository.RemoveRecording(recording);
        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("Recording {RecordingId} deleted (object + row).", recordingId);
        return new OutputDeletionResult(recordingId, RecordingType, StorageDeleted: true, RowDeleted: true);
    }

    public async Task<OutputDeletionResult> DeleteSummaryAsync(Guid summaryId, string reason, CancellationToken ct = default)
    {
        RequireReason(reason);

        // 5أ.
        var summary = await _repository.GetSummaryAsync(summaryId, ct)
            ?? throw new KeyNotFoundException("Summary not found.");

        // 5ب.
        await EnsureSessionNotLiveAsync(summary.SessionId, ct);

        // Step 6: mark PendingDeletion, then remove both files (Markdown + PDF), then the row.
        if (summary.Status != SummaryStatus.PendingDeletion)
        {
            summary.Status = SummaryStatus.PendingDeletion;
            await _repository.SaveChangesAsync(ct);
        }

        await DeleteObjectAsync(summary.MdS3Key, ct);
        await DeleteObjectAsync(summary.PdfS3Key, ct);

        _repository.RemoveSummary(summary);
        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("Summary {SummaryId} deleted (objects + row).", summaryId);
        return new OutputDeletionResult(summaryId, SummaryType, StorageDeleted: true, RowDeleted: true);
    }

    // Deletes one object-store key. A missing object is a success (6أ — the goal is already met); a
    // hard failure propagates so the deletion halts with the output left PendingDeletion (6ب). The
    // storage port already treats a missing object as success, so this is idempotent on a re-run.
    private async Task DeleteObjectAsync(string? key, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(key))
        {
            await _objectStorage.DeleteObjectAsync(key, ct);
        }
    }

    private async Task EnsureSessionNotLiveAsync(Guid sessionId, CancellationToken ct)
    {
        if (await _repository.IsSessionLiveAsync(sessionId, ct))
        {
            throw new ConflictException("The output's session is live. End the session before deleting the output.");
        }
    }

    private static void RequireReason(string? reason)
    {
        // 4أ: a reason is mandatory (also validated at the gateway).
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A deletion reason is required.");
        }
    }
}
