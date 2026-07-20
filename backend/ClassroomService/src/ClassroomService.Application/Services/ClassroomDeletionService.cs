using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Classroom;
using ClassroomService.Application.Exceptions;
using ClassroomService.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace ClassroomService.Application.Services;

/// <summary>
/// Orchestrates super-admin classroom deletion with an impact preview.
///
/// The deletion is deliberately NOT a single transaction: the use-case wants partial progress kept
/// so a failure can be resumed rather than rolled back (6أ). It runs in phases, each saving before
/// the next, and within a phase it deletes the object-storage artifact BEFORE the row that points at
/// it — so a mid-phase failure never leaves a row pointing at a deleted object, and never leaves an
/// orphaned object with no row to find it by. Because every phase re-reads what is left and every
/// delete is idempotent, re-invoking DeleteAsync on a PendingDeletion classroom simply continues
/// from where the previous run stopped.
///
/// Ordering matters: recordings and summaries are purged (objects + rows) BEFORE sessions, because
/// their rows cascade from Session — deleting sessions first would drop the rows and orphan every
/// recording/summary object in storage.
/// </summary>
public sealed class ClassroomDeletionService : IClassroomDeletionService
{
    private readonly IClassroomDeletionRepository _repository;
    private readonly IFileStorageService _fileStorage;
    private readonly IRecordingStorage _objectStorage;
    private readonly IKnowledgeInternalClient _knowledgeClient;
    private readonly ILogger<ClassroomDeletionService> _logger;

    public ClassroomDeletionService(
        IClassroomDeletionRepository repository,
        IFileStorageService fileStorage,
        IRecordingStorage objectStorage,
        IKnowledgeInternalClient knowledgeClient,
        ILogger<ClassroomDeletionService> logger)
    {
        _repository = repository;
        _fileStorage = fileStorage;
        _objectStorage = objectStorage;
        _knowledgeClient = knowledgeClient;
        _logger = logger;
    }

    public Task<ClassroomDeletionImpact?> GetImpactAsync(Guid classroomId, CancellationToken ct = default)
        => _repository.GetImpactAsync(classroomId, ct);

    public async Task<ClassroomDeletionResult> DeleteAsync(Guid classroomId, string reason, CancellationToken ct = default)
    {
        // 4أ: the reason (and, implicitly, the confirmation it accompanies) is mandatory. Validated
        // here as well as at the gateway so the deletion can never run without one.
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A deletion reason is required.");
        }

        // 5أ: the classroom must exist. Loaded tracked (any status) so a resumed run finds the row it
        // already flipped to PendingDeletion.
        var classroom = await _repository.GetTrackedAsync(classroomId, ct)
            ?? throw new KeyNotFoundException("Classroom not found.");

        // 5ب: refuse while a lecture is in progress. End the session first.
        if (await _repository.HasLiveSessionAsync(classroomId, ct))
        {
            throw new ConflictException("The classroom has a live session. End the session before deleting the classroom.");
        }

        // Step 6: take the classroom out of use first, then purge. Idempotent on a resumed run.
        if (classroom.Status != ClassroomStatus.PendingDeletion)
        {
            classroom.Status = ClassroomStatus.PendingDeletion;
            await _repository.SaveChangesAsync(ct);
        }

        // Phase 1 — recordings: S3 object first, then the row (the object key lives only on the row).
        var recordings = await _repository.GetRecordingsAsync(classroomId, ct);
        foreach (var recording in recordings)
        {
            if (!string.IsNullOrEmpty(recording.S3Key))
            {
                await _objectStorage.DeleteObjectAsync(recording.S3Key, ct);
            }
            _repository.RemoveRecording(recording);
        }
        if (recordings.Count > 0)
        {
            await _repository.SaveChangesAsync(ct);
        }

        // Phase 2 — summaries: the Markdown and the PDF rendition, then the row. Same bucket as
        // recordings, so the recording object-storage port deletes them.
        var summaries = await _repository.GetSummariesAsync(classroomId, ct);
        foreach (var summary in summaries)
        {
            if (!string.IsNullOrEmpty(summary.MdS3Key))
            {
                await _objectStorage.DeleteObjectAsync(summary.MdS3Key, ct);
            }
            if (!string.IsNullOrEmpty(summary.PdfS3Key))
            {
                await _objectStorage.DeleteObjectAsync(summary.PdfS3Key, ct);
            }
            _repository.RemoveSummary(summary);
        }
        if (summaries.Count > 0)
        {
            await _repository.SaveChangesAsync(ct);
        }

        // Phase 3 — files: object first, then the row; then de-index the classroom in KnowledgeService
        // (drops its documents, chunks and vector embeddings). De-index runs after the file rows are
        // gone and is idempotent + retried, throwing on a hard failure so the deletion halts here and
        // the classroom stays PendingDeletion for a resumable re-run.
        var files = await _repository.GetFilesAsync(classroomId, ct);
        foreach (var file in files)
        {
            await _fileStorage.DeleteFileAsync(file.S3Key, ct);
            _repository.RemoveFile(file);
        }
        if (files.Count > 0)
        {
            await _repository.SaveChangesAsync(ct);
        }
        await _knowledgeClient.DeIndexClassroomAsync(classroomId, ct);

        // Phase 4 — sessions & memberships: no object-storage artifacts of their own, so a single
        // bulk delete each.
        var sessionsDeleted = await _repository.DeleteSessionsAsync(classroomId, ct);
        var membershipsDeleted = await _repository.DeleteMembershipsAsync(classroomId, ct);

        // Phase 5 — the classroom itself.
        _repository.RemoveClassroom(classroom);
        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Classroom {ClassroomId} deleted: {Recordings} recording(s), {Summaries} summary(ies), " +
            "{Files} file(s), {Sessions} session(s), {Members} membership(s).",
            classroomId, recordings.Count, summaries.Count, files.Count, sessionsDeleted, membershipsDeleted);

        return new ClassroomDeletionResult(
            classroomId, recordings.Count, summaries.Count, files.Count, sessionsDeleted, membershipsDeleted);
    }
}
