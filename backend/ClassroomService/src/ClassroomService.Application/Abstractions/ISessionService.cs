using ClassroomService.Application.DTOs.Session;
using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Abstractions;

public interface ISessionService
{
    Task<IEnumerable<Session>> GetSessionsByClassroomAsync(Guid classroomId, CancellationToken ct = default);
    Task<Session> CreateSessionAsync(Guid classroomId, CreateSessionRequest request, CancellationToken ct = default);
    Task StartSessionAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Ends a live session on the teacher's request: the students are removed from the room and
    /// the recording/summary pipeline is kicked off. Only the classroom's own teacher may do this.
    /// Idempotent — ending an already-ended session reports it rather than failing.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such session in that classroom.</exception>
    /// <exception cref="Exceptions.ForbiddenAccessException">The caller does not teach this classroom.</exception>
    Task<SessionEndOutcome> EndSessionAsync(
        Guid classroomId, Guid sessionId, Guid requestingUserId, CancellationToken ct = default);
}