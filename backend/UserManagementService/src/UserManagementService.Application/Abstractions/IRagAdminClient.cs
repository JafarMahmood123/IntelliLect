namespace UserManagementService.Application.Abstractions;

/// <summary>
/// Reads and drives RagService's super-admin document view: the indexing status of files,
/// knowledge-base statistics, and reindex requests. The file registry itself (name/size/classroom)
/// is owned by ClassroomService — this client provides the indexing side. Calls to enrich a
/// ClassroomService-driven list must surface failures so the caller can degrade gracefully (3أ).
/// </summary>
public interface IRagAdminClient
{
    /// <summary>RagService-driven document page (used when a status filter is applied).</summary>
    Task<KnowledgeDocumentPage> ListDocumentsAsync(
        int page, int pageSize, string? status, Guid? classroomId, string? search, CancellationToken ct = default);

    /// <summary>Batch indexing status for a set of file ids (enriches a ClassroomService-driven list).</summary>
    Task<IReadOnlyList<KnowledgeDocumentItem>> GetStatusBatchAsync(
        IReadOnlyCollection<Guid> fileIds, CancellationToken ct = default);

    /// <returns>The document diagnostics, or null if no document is registered for the file (7أ).</returns>
    Task<KnowledgeDocumentDetail?> GetDocumentDetailAsync(Guid fileId, CancellationToken ct = default);

    Task<KnowledgeStatsResult> GetStatsAsync(Guid? classroomId, CancellationToken ct = default);

    /// <exception cref="Common.NotFoundException">No document registered for the file (7أ).</exception>
    /// <exception cref="InvalidOperationException">The ingestion queue is full — retry later.</exception>
    Task ReindexFileAsync(Guid fileId, CancellationToken ct = default);

    /// <exception cref="ArgumentException">The batch exceeds the reindex cap; narrow the scope (7ب).</exception>
    /// <exception cref="InvalidOperationException">A reindex is already in progress for the classroom (7ج).</exception>
    Task<BulkReindexResult> ReindexClassroomAsync(Guid classroomId, bool failedOnly, CancellationToken ct = default);
}

/// <summary>A document row from RagService (indexing side): mirrors AdminDocumentItem.</summary>
public sealed record KnowledgeDocumentItem(
    Guid FileId,
    Guid ClassroomId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Status,
    int Attempts,
    int ChunkCount);

public sealed record KnowledgeDocumentPage(
    IReadOnlyList<KnowledgeDocumentItem> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record KnowledgeDocumentDetail(
    Guid FileId,
    Guid ClassroomId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Status,
    int Attempts,
    int ChunkCount,
    string? LastError);

public sealed record KnowledgeStatsResult(
    Guid? ClassroomId,
    int DocumentCount,
    IReadOnlyDictionary<string, int> StatusCounts,
    int TotalChunks,
    int FailedCount,
    long StorageBytes);

public sealed record BulkReindexResult(Guid ClassroomId, int Requested, int Enqueued, int Skipped);
