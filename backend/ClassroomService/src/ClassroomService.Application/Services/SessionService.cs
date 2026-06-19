using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Session;
using ClassroomService.Domain.Entities;
using IntelliLect.Contracts.Messages;

namespace ClassroomService.Application.Services;

public class SessionService : ISessionService
{
    private readonly ISessionRepository _sessionRepository;
    private readonly IEventBus _eventBus;
    private readonly IClassroomRepository _classroomRepository;

    public SessionService(ISessionRepository sessionRepository, IEventBus eventBus, IClassroomRepository classroomRepository)
    {
        _sessionRepository = sessionRepository;
        _eventBus = eventBus;
        _classroomRepository = classroomRepository;
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

    public async Task StartSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await _sessionRepository.GetByIdAsync(sessionId, ct);

        if (session == null) throw new KeyNotFoundException("Session not found.");
        if (session.Status != SessionStatus.Scheduled)
            throw new InvalidOperationException("Only scheduled sessions can be started.");

        // Fetch classroom to get the TeacherId
        var classroom = await _classroomRepository.GetByIdAsync(session.ClassroomId, ct);
        if (classroom == null) throw new KeyNotFoundException("Associated classroom not found.");

        // Update Lifecycle
        session.Status = SessionStatus.Live;
        session.StartedAtUtc = DateTime.UtcNow;

        await _sessionRepository.UpdateAsync(session, ct);

        // Notify StreamingService with TeacherId
        await _eventBus.PublishAsync(new SessionStartedMessage(
            session.Id,
            session.ClassroomId,
            classroom.TeacherId), ct);

        await _sessionRepository.SaveChangesAsync(ct);
    }
}