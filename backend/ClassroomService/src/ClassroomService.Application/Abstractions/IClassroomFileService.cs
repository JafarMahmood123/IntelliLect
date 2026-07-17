using ClassroomService.Application.DTOs.File;

namespace ClassroomService.Application.Abstractions;

public interface IClassroomFileService
{
    Task<ClassroomFileResponse> UploadFileAsync(Guid classroomId, Guid uploaderId, Stream fileStream, string fileName, string contentType, CancellationToken ct = default);
    Task DeleteFileAsync(Guid fileId, Guid uploaderId, CancellationToken ct = default);
    Task<IEnumerable<ClassroomFileResponse>> GetClassroomFilesAsync(Guid classroomId, CancellationToken ct = default);

    /// <summary>
    /// Read a classroom file's RAG indexing status for a member. Missing classroom -> 404;
    /// non-member -> 403; unknown/cross-classroom file -> 404. The status is fetched from
    /// KnowledgeService server-side (no internal secret ever reaches the client).
    /// </summary>
    Task<FileIndexingStatusResponse> GetFileIndexingStatusAsync(
        Guid classroomId, Guid fileId, Guid requestingUserId, CancellationToken ct = default);
}