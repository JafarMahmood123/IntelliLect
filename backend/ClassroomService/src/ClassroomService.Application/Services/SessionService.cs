using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Session;
using ClassroomService.Application.Messages;
using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Services;

public sealed class SessionService : ISessionService
{
    private readonly ISessionRepository _sessionRepository;
    private readonly IClassroomRepository _classroomRepository;
    private readonly IEventBus _eventBus;

    public SessionService(ISessionRepository sessionRepository, IClassroomRepository classroomRepository, IEventBus eventBus)
    {
        _sessionRepository = sessionRepository;
        _classroomRepository = classroomRepository;
        _eventBus = eventBus;
    }

    public async Task StartSessionAsync(Guid teacherId, Guid sessionId)
    {
        var session = await _sessionRepository.GetByIdAsync(sessionId);
        if (session == null) throw new KeyNotFoundException();

        var classroom = await _classroomRepository.GetByIdAsync(session.ClassroomId);
        if (classroom!.TeacherId != teacherId) throw new UnauthorizedAccessException();

        session.Status = SessionStatus.Live;
        session.StartedAtUtc = DateTime.UtcNow;

        await _sessionRepository.UpdateAsync(session);
        await _sessionRepository.SaveChangesAsync();

        await _eventBus.PublishAsync(new SessionStartedMessage(session.Id, session.ClassroomId));
    }

    public async Task<Guid> ScheduleSessionAsync(Guid teacherId, Guid classroomId, CreateSessionRequest request)
    {
        var classroom = await _classroomRepository.GetByIdAsync(classroomId);
        if (classroom == null || classroom.TeacherId != teacherId)
            throw new UnauthorizedAccessException("Only the teacher can create sessions.");

        var session = new LearningSession
        {
            Id = Guid.NewGuid(),
            Title = request.Title,
            Description = request.Description,
            ClassroomId = classroomId,

            CreatedAtUtc = DateTime.UtcNow,

            ScheduledAtUtc = request.ScheduledAtUtc,

            Status = SessionStatus.Scheduled
        };

        await _sessionRepository.AddAsync(session);
        await _sessionRepository.SaveChangesAsync();

        return session.Id;
    }

    public Task EndSessionAsync(Guid teacherId, Guid sessionId)
    {
        throw new NotImplementedException();
    }
}