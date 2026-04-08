using ClassroomService.Application.DTOs.File;

namespace ClassroomService.Application.Abstractions;

public interface IClassroomFileService
{
    Task<ClassroomFileResponse> UploadFileAsync(Guid classroomId, Guid uploaderId, Stream fileStream, string fileName, string contentType, CancellationToken ct = default);
    Task DeleteFileAsync(Guid fileId, Guid uploaderId, CancellationToken ct = default);
    Task<IEnumerable<ClassroomFileResponse>> GetClassroomFilesAsync(Guid classroomId, CancellationToken ct = default);
}