namespace ClassroomService.Application.Abstractions;

public interface IStreamingInternalClient
{
    Task<bool> CreateStreamAsync(Guid sessionId, Guid classroomId, Guid teacherId, CancellationToken ct = default);
}