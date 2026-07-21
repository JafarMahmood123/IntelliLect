using ClassroomService.Application.DTOs.File;
using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Cross-classroom file access for the super-admin knowledge-base view. ClassroomService owns the
/// file registry (name/size/classroom), so it drives the list; KnowledgeService enriches indexing
/// status. Also backs the by-ids enrichment used when a status filter makes KnowledgeService the
/// list driver.
/// </summary>
public interface IFileAdminRepository
{
    Task<(List<AdminFileRow> Items, int TotalCount)> GetPagedAsync(
        string? search, Guid? classroomId, int page, int pageSize, CancellationToken ct = default);

    Task<List<AdminFileRow>> GetByIdsAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken ct = default);

    /// <summary>The tracked file entity for deletion (needs its S3 key), or null if it does not exist (7أ).</summary>
    Task<ClassroomFile?> GetByIdAsync(Guid fileId, CancellationToken ct = default);

    void Remove(ClassroomFile file);
    Task SaveChangesAsync(CancellationToken ct = default);
}
