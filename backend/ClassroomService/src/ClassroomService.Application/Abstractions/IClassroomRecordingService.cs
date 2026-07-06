using ClassroomService.Application.DTOs;
using ClassroomService.Application.DTOs.Classroom;

public interface IClassroomRecordingService
{
    Task<PagedResult<RecordingResponse>> GetClassroomRecordingsAsync(
        Guid classroomId,
        int page,
        int pageSize,
        CancellationToken ct = default);
}