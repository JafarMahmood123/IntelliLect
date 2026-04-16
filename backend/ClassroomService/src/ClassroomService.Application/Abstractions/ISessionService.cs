using ClassroomService.Application.DTOs.Session;

namespace ClassroomService.Application.Abstractions;

public interface ISessionService
{
    Task<Guid> ScheduleSessionAsync(Guid teacherId, Guid classroomId, CreateSessionRequest request);
    Task StartSessionAsync(Guid teacherId, Guid sessionId);
    Task EndSessionAsync(Guid teacherId, Guid sessionId);
}