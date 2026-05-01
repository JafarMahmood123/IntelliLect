using ClassroomService.Application.DTOs.Session;
using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Abstractions;

public interface ISessionService
{
    Task<IEnumerable<Session>> GetSessionsByClassroomAsync(Guid classroomId, CancellationToken ct = default);
    Task<Session> CreateSessionAsync(Guid classroomId, CreateSessionRequest request, CancellationToken ct = default);
}