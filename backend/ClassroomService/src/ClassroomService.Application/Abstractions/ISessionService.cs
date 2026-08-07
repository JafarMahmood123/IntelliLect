using ClassroomService.Application.DTOs.Session;
using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Abstractions;

public interface ISessionService
{
    /// <summary>The classroom's timetable, for its own members. 404 unknown classroom, 403 non-member.</summary>
    Task<IEnumerable<Session>> GetSessionsByClassroomAsync(
        Guid classroomId, Guid requestingUserId, CancellationToken ct = default);

    /// <summary>Schedules a session. Only the classroom's own teacher may — the Teacher role is not enough.</summary>
    Task<Session> CreateSessionAsync(
        Guid classroomId, Guid requestingUserId, CreateSessionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Takes a scheduled session live. Only the classroom's own teacher may; a session addressed
    /// under the wrong classroom is 404, so the route cannot be used to probe for other sessions.
    /// </summary>
    Task StartSessionAsync(
        Guid classroomId, Guid sessionId, Guid requestingUserId, CancellationToken ct = default);

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