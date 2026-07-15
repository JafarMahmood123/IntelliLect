using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;

namespace ClassroomService.Application.Abstractions;

public interface IRecordingRepository
{
    Task AddAsync(SessionRecording recording, CancellationToken ct = default);

    /// <summary>Returns the recording for a session, or null. Used by the recording-ready
    /// consumer to upsert idempotently (the R-0 Processing row, if present).</summary>
    Task<SessionRecording?> GetBySessionIdAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Returns a single recording by its id, or null (R-2 get-by-id).</summary>
    Task<SessionRecording?> GetByIdAsync(Guid recordingId, CancellationToken ct = default);

    /// <summary>
    /// Lists a classroom's recordings newest-first (R-2), optionally filtered by session and/or
    /// status, paged. Backed by the classroom_id/session_id indexes from R-1.
    /// </summary>
    Task<(IEnumerable<SessionRecording> Items, int TotalCount)> ListByClassroomAsync(
        Guid classroomId,
        Guid? sessionId,
        RecordingStatus? status,
        int page,
        int pageSize,
        CancellationToken ct = default);

    /// <summary>Recordings still in Processing whose created_at is before the cutoff (R-4 reconcile).</summary>
    Task<List<SessionRecording>> GetStuckProcessingAsync(DateTime olderThanUtc, CancellationToken ct = default);

    /// <summary>Recordings whose created_at is before the cutoff, any status (R-4 retention).</summary>
    Task<List<SessionRecording>> GetOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default);

    /// <summary>Marks the metadata row for deletion; persisted by the unit of work (R-4).</summary>
    Task RemoveAsync(SessionRecording recording, CancellationToken ct = default);

    /// <summary>Count of recordings currently in Processing (R-5 gauge refresh).</summary>
    Task<int> CountProcessingAsync(CancellationToken ct = default);
}