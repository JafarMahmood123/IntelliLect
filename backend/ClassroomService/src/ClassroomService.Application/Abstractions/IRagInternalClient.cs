using ClassroomService.Application.Models;

namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Internal HTTP client for notifying RagService about classroom file changes so
/// they get indexed. Mirrors <see cref="IStreamingInternalClient"/>. Calls are best-effort
/// from the caller's perspective — see the wiring in ClassroomFileService.
/// </summary>
public interface IRagInternalClient
{
    /// <summary>Notify RagService that a file was uploaded (enqueues ingestion).</summary>
    Task NotifyFileUploadedAsync(
        Guid fileId,
        Guid classroomId,
        string s3Key,
        string fileName,
        string contentType,
        long sizeBytes,
        CancellationToken ct = default);

    /// <summary>Notify RagService that a file was deleted (removes its index entry).</summary>
    Task NotifyFileDeletedAsync(Guid fileId, CancellationToken ct = default);

    /// <summary>
    /// De-index an entire classroom in one call: drop every document, chunk and vector embedding for
    /// it. Used by the classroom-deletion use-case instead of looping the per-file delete. Retries
    /// transient faults and throws when they are exhausted — de-indexing is a real deletion step
    /// (the classroom's indexed chunks / vector representations), so a hard failure must halt the
    /// deletion with the classroom left in PendingDeletion for a resumable re-run (6أ). Idempotent
    /// on RagService's side, so re-running is safe.
    /// </summary>
    Task DeIndexClassroomAsync(Guid classroomId, CancellationToken ct = default);

    /// <summary>
    /// Read a file's indexing status (Pending/Processing/Done/Failed) from RagService.
    /// Returns <c>null</c> when RagService has no document registered for the file yet
    /// (its 404), so the caller can present that as still-pending. The internal secret never
    /// leaves this client.
    /// </summary>
    Task<string?> GetIndexingStatusAsync(Guid fileId, CancellationToken ct = default);

    /// <summary>
    /// Run RagService's grounded RAG answer for a classroom, server-side. The classroom
    /// scope is passed explicitly by the caller (from the route + verified membership) — never
    /// from the browser. The internal secret never leaves this client.
    /// </summary>
    Task<KnowledgeAnswerResult> GetAnswerAsync(
        Guid classroomId, string question, CancellationToken ct = default);

    /// <summary>
    /// Ask RagService to generate the summary for a session (enqueues the pipeline).
    /// Best-effort: returns false instead of throwing so a force-end can complete its other
    /// steps (alternate path 7أ). The internal secret never leaves this client.
    /// </summary>
    Task<bool> TriggerSummaryAsync(Guid sessionId, Guid classroomId, CancellationToken ct = default);
}
