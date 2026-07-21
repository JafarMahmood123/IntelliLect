using UserManagementService.Application.Common;
using UserManagementService.Application.DTOs.Session;

namespace UserManagementService.Application.Abstractions;

/// <summary>
/// Super admin monitoring of learning sessions and force-ending a stalled one (use-case
/// "مراقبة جلسات التعلم والإنهاء القسري"). Aggregates ClassroomService (sessions),
/// StreamingService (live snapshot) and LiveAssistant (assistant status).
/// </summary>
public interface ISessionMonitorService
{
    Task<PagedResult<SessionMonitorItem>> GetSessionsAsync(SearchSessionsRequest request, CancellationToken ct = default);

    /// <summary>Live sessions with their real-time overlay; degrades gracefully if it can't be fetched (4أ).</summary>
    Task<LiveSessionsResponse> GetLiveSessionsAsync(CancellationToken ct = default);

    /// <exception cref="ArgumentException">No reason supplied (5أ).</exception>
    /// <exception cref="NotFoundException">The session does not exist (6أ).</exception>
    Task<ForceEndSessionResult> ForceEndAsync(Guid sessionId, string reason, CancellationToken ct = default);

    /// <returns>The deletion impact preview (step 3), or null if the session does not exist (5أ).</returns>
    Task<SessionDeletionImpactResult?> GetDeletionImpactAsync(Guid sessionId, CancellationToken ct = default);

    /// <exception cref="ArgumentException">The confirmation/reason is missing (4أ).</exception>
    Task<SessionDeletionSummary> DeleteSessionAsync(Guid sessionId, string reason, CancellationToken ct = default);
}
