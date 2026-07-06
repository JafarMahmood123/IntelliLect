using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Abstractions;

public interface IRecordingRepository
{
    Task AddAsync(SessionRecording recording, CancellationToken ct = default);

    Task<(IEnumerable<SessionRecording> Items, int TotalCount)> GetByClassroomIdPagedAsync(
        Guid classroomId,
        int page,
        int pageSize,
        CancellationToken ct = default);
}