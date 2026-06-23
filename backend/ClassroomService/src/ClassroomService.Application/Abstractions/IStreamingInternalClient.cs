using ClassroomService.Domain.Enums;

namespace ClassroomService.Application.Abstractions;

public interface IStreamingInternalClient
{
    Task<bool> CreateStreamAsync(Guid sessionId, Guid classroomId, Guid teacherId, StudentParticipationMode participationMode, CancellationToken ct = default);
}