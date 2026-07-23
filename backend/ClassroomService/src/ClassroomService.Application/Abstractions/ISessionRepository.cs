using ClassroomService.Application.DTOs.Session;
using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Abstractions;

public interface ISessionRepository
{
    Task<Session?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IEnumerable<Session>> GetByClassroomIdAsync(Guid classroomId, CancellationToken ct = default);
    Task AddAsync(Session session, CancellationToken ct = default);
    Task UpdateAsync(Session session, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Atomically moves a session from Live to Ended. The Live check is part of the write, so
    /// concurrent enders (teacher vs. the stalled sweeper, or two service instances) cannot both
    /// win — exactly one gets true and runs the teardown.
    /// </summary>
    /// <returns>true if this call performed the transition; false if the session was not Live.</returns>
    Task<bool> TryMarkEndedAsync(Guid sessionId, DateTime endedAtUtc, CancellationToken ct = default);

    /// <summary>
    /// Ids of sessions still marked Live that started at or before <paramref name="startedBeforeUtc"/> —
    /// i.e. stalled sessions the teacher never closed. A Live session with no start timestamp is
    /// judged by its creation time so it can never be stranded.
    /// </summary>
    Task<List<Guid>> GetStalledLiveSessionIdsAsync(
        DateTime startedBeforeUtc, int limit, CancellationToken ct = default);

    // Cross-classroom admin listing: joins the classroom (name/teacher) and the recording/summary
    // status, with optional free-text (title) search and status/classroom filters.
    Task<(List<AdminSessionResponse> Items, int TotalCount)> GetAdminSessionsPagedAsync(
        int page, int pageSize, string? search, SessionStatus? status, Guid? classroomId, CancellationToken ct = default);
}