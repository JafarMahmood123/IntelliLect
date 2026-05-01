using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Session;
using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Services;

public class SessionService : ISessionService
{
    private readonly ISessionRepository _sessionRepository;

    public SessionService(ISessionRepository sessionRepository)
    {
        _sessionRepository = sessionRepository;
    }

    public async Task<IEnumerable<Session>> GetSessionsByClassroomAsync(Guid classroomId, CancellationToken ct = default)
    {
        return await _sessionRepository.GetByClassroomIdAsync(classroomId, ct);
    }

    public async Task<Session> CreateSessionAsync(Guid classroomId, CreateSessionRequest request, CancellationToken ct = default)
    {
        var session = new Session
        {
            Id = Guid.NewGuid(),
            ClassroomId = classroomId,
            Title = request.Title,
            Description = request.Description,
            ScheduledAtUtc = request.ScheduledAtUtc,

            // Backend-managed lifecycle fields
            CreatedAtUtc = DateTime.UtcNow,
            Status = SessionStatus.Scheduled,
            StartedAtUtc = null,
            EndedAtUtc = null
        };

        await _sessionRepository.AddAsync(session, ct);
        await _sessionRepository.SaveChangesAsync(ct);

        return session;
    }
}