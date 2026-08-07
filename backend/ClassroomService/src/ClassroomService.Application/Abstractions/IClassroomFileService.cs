using ClassroomService.Application.DTOs.File;

namespace ClassroomService.Application.Abstractions;

public interface IClassroomFileService
{
    /// <summary>
    /// The upload control's bounds, so it cannot offer a file the server will reject. Advisory on
    /// the client; <see cref="UploadFileAsync"/> enforces the same values regardless.
    /// </summary>
    UploadLimitsDto GetUploadLimits();

    /// <summary>
    /// Stores a classroom material file. Non-teacher -> 401; empty or oversized file -> 422/413;
    /// a format no extractor handles -> 422. A rejected upload writes nothing: no S3 object, no row.
    /// </summary>
    Task<ClassroomFileResponse> UploadFileAsync(Guid classroomId, Guid uploaderId, Stream fileStream, string fileName, string contentType, CancellationToken ct = default);
    Task DeleteFileAsync(Guid fileId, Guid uploaderId, CancellationToken ct = default);
    /// <summary>The material list, for this classroom's own members. 404 unknown classroom, 403 non-member.</summary>
    Task<IEnumerable<ClassroomFileResponse>> GetClassroomFilesAsync(
        Guid classroomId, Guid requestingUserId, CancellationToken ct = default);

    /// <summary>
    /// Opens a classroom material file for download by a member, streamed through the API (and
    /// thus the gateway) rather than a direct-to-MinIO link — so the browser stays on the app's
    /// origin. Missing classroom -> 404; non-member -> 403; unknown/cross-classroom file -> 404.
    /// </summary>
    Task<FileDownloadResult> GetFileDownloadAsync(
        Guid classroomId, Guid fileId, Guid requestingUserId, CancellationToken ct = default);

    /// <summary>
    /// Read a classroom file's RAG indexing status for a member. Missing classroom -> 404;
    /// non-member -> 403; unknown/cross-classroom file -> 404. The status is fetched from
    /// RagService server-side (no internal secret ever reaches the client).
    /// </summary>
    Task<FileIndexingStatusResponse> GetFileIndexingStatusAsync(
        Guid classroomId, Guid fileId, Guid requestingUserId, CancellationToken ct = default);
}