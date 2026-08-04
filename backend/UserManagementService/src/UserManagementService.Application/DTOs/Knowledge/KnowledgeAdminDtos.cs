namespace UserManagementService.Application.DTOs.Knowledge;

/// <summary>Query parameters for the super-admin knowledge-base file listing.</summary>
public sealed class SearchFilesRequest
{
    public string? Search { get; set; }
    /// <summary>Indexing status filter (Pending/Processing/Done/Failed). When set, RagService drives.</summary>
    public string? Status { get; set; }
    public Guid? ClassroomId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

/// <summary>
/// A file row for the super-admin knowledge-base view. Registry fields (name/size/classroom) come
/// from ClassroomService; indexing fields (status/attempts/chunkCount) from RagService and are
/// null when the indexing status could not be fetched (3أ).
/// </summary>
public sealed record AdminFileItem(
    Guid FileId,
    Guid ClassroomId,
    string? ClassName,
    string FileName,
    string ContentType,
    long SizeBytes,
    string? Status,
    int? Attempts,
    int? ChunkCount);

/// <summary>A page of files. <see cref="IndexingUnavailable"/> is true when indexing status could
/// not be fetched, so the rows are shown without it (3أ).</summary>
public sealed record FileListResult(
    IReadOnlyList<AdminFileItem> Items,
    int TotalCount,
    int PageNumber,
    int PageSize,
    bool IndexingUnavailable);

/// <summary>A file's diagnostics (step 4): status, attempts and the failure reason.</summary>
public sealed record FileDetailResult(
    Guid FileId,
    Guid ClassroomId,
    string? ClassName,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Status,
    int Attempts,
    int ChunkCount,
    string? LastError);

/// <summary>Knowledge-base statistics (step 5).</summary>
public sealed record KnowledgeStatsResponse(
    Guid? ClassroomId,
    int DocumentCount,
    IReadOnlyDictionary<string, int> StatusCounts,
    int TotalChunks,
    int FailedCount,
    long StorageBytes);

/// <summary>Body for reindexing a single file; the reason is mandatory (6أ).</summary>
public sealed record ReindexFileRequest(string Reason);

/// <summary>Body for reindexing a classroom; the reason is mandatory (6أ). FailedOnly narrows scope (7ب).</summary>
public sealed record ReindexClassroomRequest(bool FailedOnly, string Reason);

/// <summary>Body for deleting a file; the reason is mandatory (6أ).</summary>
public sealed record DeleteFileAdminRequest(string Reason);

/// <summary>Result of a classroom bulk reindex (step 7 / 7د).</summary>
public sealed record BulkReindexResponse(Guid ClassroomId, int Requested, int Enqueued, int Skipped);

/// <summary>Result of a file deletion (step 7 / 7هـ).</summary>
public sealed record FileDeletionResponse(Guid FileId, bool StorageDeleted, bool DeIndexed);
