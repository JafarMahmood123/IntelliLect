using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs;
using ClassroomService.Application.DTOs.Session;
using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Services;

public sealed class SessionAdminService : ISessionAdminService
{
    private readonly ISessionRepository _sessionRepository;
    private readonly ISessionTerminationService _termination;

    public SessionAdminService(
        ISessionRepository sessionRepository,
        ISessionTerminationService termination)
    {
        _sessionRepository = sessionRepository;
        _termination = termination;
    }

    public async Task<PagedResult<AdminSessionResponse>> GetSessionsAsync(
        int page, int pageSize, string? search, SessionStatus? status, Guid? classroomId, CancellationToken ct = default)
    {
        var (items, totalCount) = await _sessionRepository.GetAdminSessionsPagedAsync(page, pageSize, search, status, classroomId, ct);
        return new PagedResult<AdminSessionResponse>(items, totalCount, page, pageSize);
    }

    public async Task<ForceEndSessionResponse> ForceEndAsync(Guid sessionId, string reason, CancellationToken ct = default)
    {
        // Alternate path 5أ: a reason is mandatory (also validated upstream).
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason for force-ending the session is required.");
        }

        // Step 7 runs through the shared termination path, so a force-end tears the session down
        // exactly like a teacher-initiated end: Ended is committed first (the postcondition holds
        // even if a downstream best-effort step fails, 7أ), then the stream is closed and the
        // summary triggered. 6أ (unknown session) surfaces as KeyNotFoundException; 6ب (not live)
        // comes back as a no-op outcome.
        var outcome = await _termination.EndAsync(sessionId, SessionEndTrigger.SuperAdmin, reason, ct);

        return new ForceEndSessionResponse(
            outcome.SessionId, outcome.Status,
            outcome.AlreadyEnded, outcome.StreamEnded, outcome.SummaryTriggered);
    }
}
