using UserManagementService.Application.DTOs.Knowledge;

namespace UserManagementService.Application.Abstractions;

/// <summary>
/// Super-admin content &amp; knowledge-base management (use-case "إدارة المحتوى وقاعدة المعرفة").
/// Aggregates ClassroomService (the authoritative file registry) and RagService (indexing
/// status, statistics, reindex). The file list is driven by ClassroomService so it still renders
/// when indexing status can't be fetched (3أ); a status filter makes RagService the driver.
/// </summary>
public interface IKnowledgeAdminService
{
    Task<FileListResult> GetFilesAsync(SearchFilesRequest request, CancellationToken ct = default);

    /// <returns>The file diagnostics, or null if no document is registered for it (7أ).</returns>
    Task<FileDetailResult?> GetFileDetailAsync(Guid fileId, CancellationToken ct = default);

    Task<KnowledgeStatsResponse> GetStatsAsync(Guid? classroomId, CancellationToken ct = default);

    /// <exception cref="ArgumentException">No reason supplied (6أ).</exception>
    Task ReindexFileAsync(Guid fileId, ReindexFileRequest request, CancellationToken ct = default);

    /// <exception cref="ArgumentException">No reason supplied (6أ), or the batch exceeds the cap (7ب).</exception>
    /// <exception cref="InvalidOperationException">A reindex is already in progress for the classroom (7ج).</exception>
    Task<BulkReindexResponse> ReindexClassroomAsync(Guid classroomId, ReindexClassroomRequest request, CancellationToken ct = default);

    /// <exception cref="ArgumentException">No reason supplied (6أ).</exception>
    /// <exception cref="NotFoundException">The file does not exist (7أ).</exception>
    Task<FileDeletionResponse> DeleteFileAsync(Guid fileId, DeleteFileAdminRequest request, CancellationToken ct = default);
}
