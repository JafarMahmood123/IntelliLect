using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Abstractions;

public interface IRecordingRepository
{
    Task AddAsync(SessionRecording recording, CancellationToken ct = default);

    /// <summary>Returns the recording for a session, or null. Used by the recording-ready
    /// consumer to upsert idempotently (the R-0 Processing row, if present).</summary>
    Task<SessionRecording?> GetBySessionIdAsync(Guid sessionId, CancellationToken ct = default);

    Task<(IEnumerable<SessionRecording> Items, int TotalCount)> GetByClassroomIdPagedAsync(
        Guid classroomId,
        int page,
        int pageSize,
        CancellationToken ct = default);
}