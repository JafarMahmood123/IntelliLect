using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.File;
using Microsoft.Extensions.Logging;

namespace ClassroomService.Application.Services;

public sealed class FileAdminService : IFileAdminService
{
    private readonly IFileAdminRepository _repository;
    private readonly IFileStorageService _storage;
    private readonly IKnowledgeInternalClient _knowledgeClient;
    private readonly ILogger<FileAdminService> _logger;

    public FileAdminService(
        IFileAdminRepository repository,
        IFileStorageService storage,
        IKnowledgeInternalClient knowledgeClient,
        ILogger<FileAdminService> logger)
    {
        _repository = repository;
        _storage = storage;
        _knowledgeClient = knowledgeClient;
        _logger = logger;
    }

    public async Task<AdminFilePage> GetFilesAsync(
        string? search, Guid? classroomId, int page, int pageSize, CancellationToken ct = default)
    {
        var normalizedPage = page < 1 ? 1 : page;
        var normalizedPageSize = Math.Clamp(pageSize < 1 ? 20 : pageSize, 1, 100);

        var (items, total) = await _repository.GetPagedAsync(search, classroomId, normalizedPage, normalizedPageSize, ct);
        return new AdminFilePage(items, total, normalizedPage, normalizedPageSize);
    }

    public async Task<IReadOnlyList<AdminFileRow>> GetFilesByIdsAsync(
        IReadOnlyCollection<Guid> fileIds, CancellationToken ct = default)
        => await _repository.GetByIdsAsync(fileIds, ct);

    /// <summary>
    /// Step 7 delete order: object store first, then the indexed chunks/vectors (KnowledgeService),
    /// then the metadata row. De-index must succeed before the row is removed, so a de-index failure
    /// (7هـ) halts here with the row intact — re-running deletes the (idempotent) object again, retries
    /// the (idempotent) de-index, then removes the row, completing without repeating finished work.
    /// </summary>
    public async Task<AdminFileDeletionResult> DeleteFileAsync(Guid fileId, string reason, CancellationToken ct = default)
    {
        // 6أ: reason required (also validated at the gateway).
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A deletion reason is required.");
        }

        // 7أ: the file must exist.
        var file = await _repository.GetByIdAsync(fileId, ct)
            ?? throw new KeyNotFoundException("File not found.");

        // 1. Delete the object from the file store (idempotent on S3).
        await _storage.DeleteFileAsync(file.S3Key, ct);

        // 2. De-index: drop its chunks + vector embeddings. NOT best-effort here — a hard failure
        //    throws and halts before the row is removed, so the delete is resumable (7هـ).
        await _knowledgeClient.NotifyFileDeletedAsync(fileId, ct);

        // 3. Remove the metadata row.
        _repository.Remove(file);
        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("File {FileId} deleted (object + index + row).", fileId);
        return new AdminFileDeletionResult(fileId, StorageDeleted: true, DeIndexed: true);
    }
}
