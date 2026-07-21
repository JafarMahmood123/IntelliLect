using UserManagementService.Application.Abstractions;
using UserManagementService.Application.DTOs.Knowledge;

namespace UserManagementService.Application.KnowledgeAdministration;

public sealed class KnowledgeAdminService : IKnowledgeAdminService
{
    private readonly IClassroomInternalClient _classroomClient;
    private readonly IKnowledgeAdminClient _knowledgeClient;

    public KnowledgeAdminService(
        IClassroomInternalClient classroomClient,
        IKnowledgeAdminClient knowledgeClient)
    {
        _classroomClient = classroomClient;
        _knowledgeClient = knowledgeClient;
    }

    public async Task<FileListResult> GetFilesAsync(SearchFilesRequest request, CancellationToken ct = default)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = Math.Clamp(request.PageSize < 1 ? 20 : request.PageSize, 1, 100);

        return string.IsNullOrWhiteSpace(request.Status)
            ? await GetFilesClassroomDrivenAsync(request, page, pageSize, ct)
            : await GetFilesKnowledgeDrivenAsync(request, page, pageSize, ct);
    }

    // No status filter: ClassroomService (the authoritative registry) drives the page; indexing status
    // is enrichment, and a failure to fetch it degrades to "unavailable" without hiding the list (3أ).
    private async Task<FileListResult> GetFilesClassroomDrivenAsync(
        SearchFilesRequest request, int page, int pageSize, CancellationToken ct)
    {
        var filePage = await _classroomClient.GetFilesAsync(page, pageSize, request.Search, request.ClassroomId, ct);
        var files = filePage.Items;
        var fileIds = files.Select(f => f.FileId).ToList();

        var indexingUnavailable = false;
        var statusByFile = new Dictionary<Guid, KnowledgeDocumentItem>();
        if (fileIds.Count > 0)
        {
            try
            {
                var statuses = await _knowledgeClient.GetStatusBatchAsync(fileIds, ct);
                statusByFile = statuses.ToDictionary(s => s.FileId);
            }
            catch (Exception)
            {
                // 3أ: indexing status is temporarily unavailable; still show the files.
                indexingUnavailable = true;
            }
        }

        var names = await LoadClassroomNamesAsync(files.Select(f => f.ClassroomId), ct);

        var items = files.Select(f =>
        {
            names.TryGetValue(f.ClassroomId, out var className);
            statusByFile.TryGetValue(f.FileId, out var status);
            return new AdminFileItem(
                f.FileId, f.ClassroomId, className, f.FileName, f.ContentType, f.SizeBytes,
                indexingUnavailable ? null : status?.Status,
                indexingUnavailable ? null : status?.Attempts,
                indexingUnavailable ? null : status?.ChunkCount);
        }).ToList();

        return new FileListResult(items, filePage.TotalCount, page, pageSize, indexingUnavailable);
    }

    // Status filter set: only KnowledgeService can page by indexing status, so it drives; the file
    // registry (authoritative name/size) is enriched from ClassroomService, falling back to the
    // denormalized values when a row can't be resolved.
    private async Task<FileListResult> GetFilesKnowledgeDrivenAsync(
        SearchFilesRequest request, int page, int pageSize, CancellationToken ct)
    {
        var docPage = await _knowledgeClient.ListDocumentsAsync(
            page, pageSize, request.Status, request.ClassroomId, request.Search, ct);
        var docs = docPage.Items;
        var fileIds = docs.Select(d => d.FileId).ToList();

        var filesById = new Dictionary<Guid, AdminFile>();
        try
        {
            var files = await _classroomClient.GetFilesByIdsAsync(fileIds, ct);
            filesById = files.ToDictionary(f => f.FileId);
        }
        catch (Exception)
        {
            // Registry enrichment is best-effort here; the denormalized name/size cover the gap.
        }

        var names = await LoadClassroomNamesAsync(docs.Select(d => d.ClassroomId), ct);

        var items = docs.Select(d =>
        {
            names.TryGetValue(d.ClassroomId, out var className);
            filesById.TryGetValue(d.FileId, out var file);
            return new AdminFileItem(
                d.FileId, d.ClassroomId, className,
                file?.FileName ?? d.FileName,
                file?.ContentType ?? d.ContentType,
                file?.SizeBytes ?? d.SizeBytes,
                d.Status, d.Attempts, d.ChunkCount);
        }).ToList();

        return new FileListResult(items, docPage.Total, page, pageSize, false);
    }

    public async Task<FileDetailResult?> GetFileDetailAsync(Guid fileId, CancellationToken ct = default)
    {
        var detail = await _knowledgeClient.GetDocumentDetailAsync(fileId, ct);
        if (detail is null)
        {
            return null; // 7أ
        }

        var names = await LoadClassroomNamesAsync(new[] { detail.ClassroomId }, ct);
        names.TryGetValue(detail.ClassroomId, out var className);

        // Prefer the authoritative registry values for name/size when reachable.
        AdminFile? file = null;
        try
        {
            file = (await _classroomClient.GetFilesByIdsAsync(new[] { fileId }, ct)).FirstOrDefault();
        }
        catch (Exception)
        {
            // best-effort
        }

        return new FileDetailResult(
            detail.FileId, detail.ClassroomId, className,
            file?.FileName ?? detail.FileName,
            file?.ContentType ?? detail.ContentType,
            file?.SizeBytes ?? detail.SizeBytes,
            detail.Status, detail.Attempts, detail.ChunkCount, detail.LastError);
    }

    public async Task<KnowledgeStatsResponse> GetStatsAsync(Guid? classroomId, CancellationToken ct = default)
    {
        var stats = await _knowledgeClient.GetStatsAsync(classroomId, ct);
        return new KnowledgeStatsResponse(
            stats.ClassroomId, stats.DocumentCount, stats.StatusCounts,
            stats.TotalChunks, stats.FailedCount, stats.StorageBytes);
    }

    public async Task ReindexFileAsync(Guid fileId, ReindexFileRequest request, CancellationToken ct = default)
    {
        RequireReason(request?.Reason);
        // NotFoundException (7أ) / InvalidOperationException (queue full) propagate from the client.
        await _knowledgeClient.ReindexFileAsync(fileId, ct);
    }

    public async Task<BulkReindexResponse> ReindexClassroomAsync(
        Guid classroomId, ReindexClassroomRequest request, CancellationToken ct = default)
    {
        RequireReason(request?.Reason);
        // ArgumentException (7ب) / InvalidOperationException (7ج) propagate from the client.
        var result = await _knowledgeClient.ReindexClassroomAsync(classroomId, request!.FailedOnly, ct);
        return new BulkReindexResponse(result.ClassroomId, result.Requested, result.Enqueued, result.Skipped);
    }

    public async Task<FileDeletionResponse> DeleteFileAsync(
        Guid fileId, DeleteFileAdminRequest request, CancellationToken ct = default)
    {
        RequireReason(request?.Reason);
        // NotFoundException (7أ) propagates from the client. A de-index failure (7هـ) surfaces as an
        // error from ClassroomService, leaving the file resumable.
        var result = await _classroomClient.DeleteFileAsync(fileId, request!.Reason.Trim(), ct);
        return new FileDeletionResponse(result.FileId, result.StorageDeleted, result.DeIndexed);
    }

    // Resolves classroom names for the given ids; best-effort so a name-lookup failure never blocks
    // the list (the names are cosmetic — the ids are always present).
    private async Task<Dictionary<Guid, string>> LoadClassroomNamesAsync(
        IEnumerable<Guid> classroomIds, CancellationToken ct)
    {
        var ids = classroomIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        try
        {
            var names = await _classroomClient.GetClassroomNamesAsync(ids, ct);
            return names.ToDictionary(n => n.Id, n => n.Name);
        }
        catch (Exception)
        {
            return new Dictionary<Guid, string>();
        }
    }

    private static void RequireReason(string? reason)
    {
        // 6أ: every reindex/delete action requires a reason.
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason for the action is required.");
        }
    }
}
