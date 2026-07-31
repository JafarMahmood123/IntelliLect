using ClassroomService.Domain.Enums;

namespace ClassroomService.Application.Abstractions;

public interface IStreamingInternalClient
{
    Task<bool> CreateStreamAsync(Guid sessionId, Guid classroomId, Guid teacherId, StudentParticipationMode participationMode, bool recordingEnabled, CancellationToken ct = default);

    /// <summary>
    /// Runs StreamingService's session-end path (stop recording egress, notify the AI assistant
    /// to stop, close the room). Best-effort: returns false instead of throwing so a force-end can
    /// continue its remaining steps (alternate path 7أ).
    /// </summary>
    Task<bool> EndStreamAsync(Guid sessionId, CancellationToken ct = default);
}