using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Persistence for session summaries (S-4). Mirrors <see cref="IRecordingRepository"/>: upsert by
/// session id (idempotent consumer), get-by-id, and a classroom-scoped newest-first listing.
/// </summary>
public interface ISummaryRepository
{
    Task AddAsync(SessionSummary summary, CancellationToken ct = default);

    /// <summary>Returns the summary for a session, or null. Used by the summary-ready consumer to
    /// upsert idempotently (a Generating row, if one exists).</summary>
    Task<SessionSummary?> GetBySessionIdAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Returns a single summary by its id, or null.</summary>
    Task<SessionSummary?> GetByIdAsync(Guid summaryId, CancellationToken ct = default);

    /// <summary>
    /// Lists a classroom's summaries newest-first, optionally filtered by session, paged. Backed by
    /// the classroom_id/session_id indexes.
    /// </summary>
    Task<(IEnumerable<SessionSummary> Items, int TotalCount)> ListByClassroomAsync(
        Guid classroomId,
        Guid? sessionId,
        int page,
        int pageSize,
        CancellationToken ct = default);
}
