using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs;
using ClassroomService.Application.DTOs.Classroom;

namespace ClassroomService.Application.Services;

public sealed class ClassroomRecordingService : IClassroomRecordingService
{
    private readonly IRecordingRepository _recordingRepository;

    public ClassroomRecordingService(IRecordingRepository recordingRepository)
    {
        _recordingRepository = recordingRepository;
    }

    public async Task<PagedResult<RecordingResponse>> GetClassroomRecordingsAsync(
        Guid classroomId, int page, int pageSize, CancellationToken ct = default)
    {
        var (items, totalCount) = await _recordingRepository.GetByClassroomIdPagedAsync(
            classroomId, page, pageSize, ct);

        var mappedItems = items.Select(r => new RecordingResponse(
            r.Id,
            r.SessionId,
            r.Session.Title,
            r.CreatedAtUtc,
            r.SizeBytes,
            $"http://localhost:9000/intellilect-files/{r.S3Key}"
        )).ToList();

        return new PagedResult<RecordingResponse>(mappedItems, totalCount, page, pageSize);
    }
}