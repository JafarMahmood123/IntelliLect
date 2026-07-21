using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Session;
using ClassroomService.Application.Exceptions;
using ClassroomService.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace ClassroomService.Application.Services;

/// <summary>
/// Orchestrates super-admin deletion of a session and its outputs.
///
/// Like the classroom deletion, this is deliberately NOT one transaction: the use-case wants partial
/// progress kept so a failure can be resumed rather than rolled back (6ب). The session is marked
/// PendingDeletion first (hiding it from teacher/student lists), then each output is purged
/// object-before-row so a mid-step failure never leaves a row pointing at a deleted object. Missing
/// outputs are simply skipped (6أ). Because every step re-reads what is left and every delete is
/// idempotent, re-invoking DeleteAsync on a PendingDeletion session resumes from where it stopped.
///
/// The transcript lives in LiveAssistantService, so it is deleted over HTTP (idempotent; a 404 means
/// there was none). A hard failure there throws and halts the deletion with the session still
/// PendingDeletion.
/// </summary>
public sealed class SessionDeletionService : ISessionDeletionService
{
    private readonly ISessionDeletionRepository _repository;
    private readonly IRecordingStorage _objectStorage;
    private readonly ILiveAssistantInternalClient _liveAssistant;
    private readonly ILogger<SessionDeletionService> _logger;

    public SessionDeletionService(
        ISessionDeletionRepository repository,
        IRecordingStorage objectStorage,
        ILiveAssistantInternalClient liveAssistant,
        ILogger<SessionDeletionService> logger)
    {
        _repository = repository;
        _objectStorage = objectStorage;
        _liveAssistant = liveAssistant;
        _logger = logger;
    }

    public async Task<SessionDeletionImpact?> GetImpactAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await _repository.GetTrackedAsync(sessionId, ct);
        if (session is null)
        {
            return null; // 5أ
        }

        var recording = await _repository.GetRecordingAsync(sessionId, ct);
        var summary = await _repository.GetSummaryAsync(sessionId, ct);

        // Transcript presence is owned by LiveAssistantService. Best-effort: if it cannot be reached,
        // report the transcript as unavailable rather than blocking the whole preview.
        bool hasTranscript = false;
        bool transcriptUnavailable = false;
        try
        {
            var segmentCount = await _liveAssistant.GetTranscriptSegmentCountAsync(sessionId, ct);
            hasTranscript = segmentCount is > 0;
        }
        catch (Exception ex)
        {
            transcriptUnavailable = true;
            _logger.LogWarning(ex, "Could not check transcript for session {SessionId} during impact preview.", sessionId);
        }

        var storageBytes = recording?.SizeBytes ?? 0L;

        return new SessionDeletionImpact(
            session.Id,
            session.Title,
            session.Status.ToString(),
            HasRecording: recording is not null,
            HasSummary: summary is not null,
            HasTranscript: hasTranscript,
            StorageBytes: storageBytes,
            IsLive: session.Status == SessionStatus.Live,
            TranscriptUnavailable: transcriptUnavailable);
    }

    public async Task<SessionDeletionResult> DeleteAsync(Guid sessionId, string reason, CancellationToken ct = default)
    {
        // 4أ: the reason (and thus the confirmation) is mandatory.
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A deletion reason is required.");
        }

        // 5أ: the session must exist. Loaded tracked (any status) so a resumed run finds the row it
        // already flipped to PendingDeletion.
        var session = await _repository.GetTrackedAsync(sessionId, ct)
            ?? throw new KeyNotFoundException("Session not found.");

        // 5ب: refuse while the session is being broadcast. End it first.
        if (session.Status == SessionStatus.Live)
        {
            throw new ConflictException("The session is live. End the session before deleting it.");
        }

        // Step 6: take the session out of use, then purge. Idempotent on a resumed run.
        if (session.Status != SessionStatus.PendingDeletion)
        {
            session.Status = SessionStatus.PendingDeletion;
            await _repository.SaveChangesAsync(ct);
        }

        // Recording: S3 object first, then the row. Skipped when absent (6أ).
        var recording = await _repository.GetRecordingAsync(sessionId, ct);
        var recordingDeleted = false;
        if (recording is not null)
        {
            if (!string.IsNullOrEmpty(recording.S3Key))
            {
                await _objectStorage.DeleteObjectAsync(recording.S3Key, ct);
            }
            _repository.RemoveRecording(recording);
            await _repository.SaveChangesAsync(ct);
            recordingDeleted = true;
        }

        // Summary: the Markdown and PDF objects, then the row. Skipped when absent (6أ).
        var summary = await _repository.GetSummaryAsync(sessionId, ct);
        var summaryDeleted = false;
        if (summary is not null)
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
            await _repository.SaveChangesAsync(ct);
            summaryDeleted = true;
        }

        // Transcript (LiveAssistantService): idempotent; a 404 means there was none (6أ). A hard
        // failure throws and halts the deletion with the session still PendingDeletion.
        await _liveAssistant.DeleteSessionTranscriptAsync(sessionId, ct);

        // Finally the session row itself.
        _repository.RemoveSession(session);
        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Session {SessionId} deleted: recording={Recording}, summary={Summary}, transcript purged.",
            sessionId, recordingDeleted, summaryDeleted);

        return new SessionDeletionResult(sessionId, recordingDeleted, summaryDeleted, TranscriptDeleted: true);
    }
}
