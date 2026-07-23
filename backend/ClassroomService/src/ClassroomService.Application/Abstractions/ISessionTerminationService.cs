using ClassroomService.Application.DTOs.Session;

namespace ClassroomService.Application.Abstractions;

/// <summary>
/// The single place a live session is brought to <c>Ended</c>. Every caller that closes a session
/// goes through here so the teardown is identical regardless of who asked:
///   - the teacher pressing "End Session" (<see cref="SessionEndTrigger.Teacher"/>),
///   - the super admin force-ending it (<see cref="SessionEndTrigger.SuperAdmin"/>),
///   - the stalled-session sweeper (<see cref="SessionEndTrigger.StalledSweep"/>).
/// Ending is idempotent and safe to race: the Live -> Ended transition is claimed atomically, so
/// only one caller runs the teardown even if a teacher and the sweeper fire at the same moment.
/// </summary>
public interface ISessionTerminationService
{
    /// <param name="reason">Free-text audit note; logged with the outcome.</param>
    /// <exception cref="KeyNotFoundException">The session does not exist.</exception>
    Task<SessionEndOutcome> EndAsync(
        Guid sessionId, SessionEndTrigger trigger, string reason, CancellationToken ct = default);
}
