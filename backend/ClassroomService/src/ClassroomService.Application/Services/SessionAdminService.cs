using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs;
using ClassroomService.Application.DTOs.Session;
using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Services;

public sealed class SessionAdminService : ISessionAdminService
{
    private readonly ISessionRepository _sessionRepository;
    private readonly IStreamingInternalClient _streamingClient;
    private readonly IKnowledgeInternalClient _knowledgeClient;

    public SessionAdminService(
        ISessionRepository sessionRepository,
        IStreamingInternalClient streamingClient,
        IKnowledgeInternalClient knowledgeClient)
    {
        _sessionRepository = sessionRepository;
        _streamingClient = streamingClient;
        _knowledgeClient = knowledgeClient;
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

        var session = await _sessionRepository.GetByIdAsync(sessionId, ct);

        // Alternate path 6أ: the session does not exist.
        if (session == null)
        {
            throw new KeyNotFoundException("Session not found.");
        }

        // Alternate path 6ب: only a live session can be force-ended. Anything else is a no-op —
        // the caller is told it is already ended / not active, and nothing is changed.
        if (session.Status != SessionStatus.Live)
        {
            return new ForceEndSessionResponse(
                sessionId, session.Status.ToString(),
                AlreadyEnded: session.Status == SessionStatus.Ended,
                StreamEnded: false, SummaryTriggered: false);
        }

        // Step 7: the session reaches Ended FIRST and is committed, so the postcondition holds
        // even if a downstream best-effort step fails (alternate path 7أ).
        session.Status = SessionStatus.Ended;
        session.EndedAtUtc = DateTime.UtcNow;
        await _sessionRepository.UpdateAsync(session, ct);
        await _sessionRepository.SaveChangesAsync(ct);

        // Step 7 (cont.), best-effort: run the stream-end path (stop recording, stop the AI
        // assistant, close the room), then trigger summary generation. Each returns a flag rather
        // than throwing, so one failure never blocks the other or the overall force-end.
        var streamEnded = await _streamingClient.EndStreamAsync(sessionId, ct);
        var summaryTriggered = await _knowledgeClient.TriggerSummaryAsync(sessionId, session.ClassroomId, ct);

        return new ForceEndSessionResponse(
            sessionId, SessionStatus.Ended.ToString(),
            AlreadyEnded: false, streamEnded, summaryTriggered);
    }
}
