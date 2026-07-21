using ClassroomService.Application.DTOs.File;

namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Super-admin file access for the knowledge-base view: the authoritative file list (ClassroomService
/// owns the registry) and file deletion (store object → de-index → row, resumable per 7هـ).
/// </summary>
public interface IFileAdminService
{
    Task<AdminFilePage> GetFilesAsync(string? search, Guid? classroomId, int page, int pageSize, CancellationToken ct = default);

    Task<IReadOnlyList<AdminFileRow>> GetFilesByIdsAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken ct = default);

    /// <exception cref="System.ArgumentException">The reason is missing (6أ).</exception>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">The file does not exist (7أ).</exception>
    Task<AdminFileDeletionResult> DeleteFileAsync(Guid fileId, string reason, CancellationToken ct = default);
}
