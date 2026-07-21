namespace ClassroomService.Application.DTOs.Session;

/// <summary>
/// Read-only preview of what deleting a session will destroy (step 3): whether it has a recording,
/// a summary and a transcript, and the object storage that will be freed (recording bytes only —
/// summaries and transcripts carry no recorded size). <see cref="IsLive"/> reflects precondition 5ب
/// (a live session cannot be deleted). <see cref="TranscriptUnavailable"/> is set when
/// LiveAssistantService could not be reached to check the transcript, so the flag is best-effort.
/// </summary>
public sealed record SessionDeletionImpact(
    Guid SessionId,
    string Title,
    string Status,
    bool HasRecording,
    bool HasSummary,
    bool HasTranscript,
    long StorageBytes,
    bool IsLive,
    bool TranscriptUnavailable);

/// <summary>
/// Outcome of a completed session deletion (step 8): which outputs this pass removed. On a resumed
/// run, an output an earlier pass already deleted reports false — nothing was left for this pass.
/// </summary>
public sealed record SessionDeletionResult(
    Guid SessionId,
    bool RecordingDeleted,
    bool SummaryDeleted,
    bool TranscriptDeleted);
