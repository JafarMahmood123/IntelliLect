using ClassroomService.Application.DTOs.Output;
using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Persistence for the super-admin recordings/summaries management use-case. Recordings and summaries
/// both live in ClassroomService, so their combined listing and their deletion (object-store-first,
/// then row) all live here. Every fetch/remove is idempotent, which is what makes a failed deletion
/// re-runnable (6ب).
/// </summary>
public interface IOutputAdminRepository
{
    /// <summary>
    /// A page of session outputs (recordings + summaries), newest-first, with optional filters:
    /// type ("Recording"/"Summary"), status, classroom, and a session-title search. Keeps
    /// PendingDeletion rows visible so a stuck deletion can be seen and retried.
    /// </summary>
    Task<(List<AdminOutputRow> Items, int TotalCount)> GetOutputsPagedAsync(
        string? search, string? type, string? status, Guid? classroomId, int page, int pageSize, CancellationToken ct = default);

    /// <summary>The tracked recording (needs its S3 key), or null if it does not exist (5أ).</summary>
    Task<SessionRecording?> GetRecordingAsync(Guid recordingId, CancellationToken ct = default);

    /// <summary>The tracked summary (needs its S3 keys), or null if it does not exist (5أ).</summary>
    Task<SessionSummary?> GetSummaryAsync(Guid summaryId, CancellationToken ct = default);

    /// <summary>True if the given session is currently Live (precondition 5ب).</summary>
    Task<bool> IsSessionLiveAsync(Guid sessionId, CancellationToken ct = default);

    void RemoveRecording(SessionRecording recording);
    void RemoveSummary(SessionSummary summary);
    Task SaveChangesAsync(CancellationToken ct = default);
}
