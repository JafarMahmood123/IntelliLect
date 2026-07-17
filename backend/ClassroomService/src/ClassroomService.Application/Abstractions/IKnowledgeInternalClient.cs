namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Internal HTTP client for notifying KnowledgeService about classroom file changes so
/// they get indexed. Mirrors <see cref="IStreamingInternalClient"/>. Calls are best-effort
/// from the caller's perspective — see the wiring in ClassroomFileService.
/// </summary>
public interface IKnowledgeInternalClient
{
    /// <summary>Notify KnowledgeService that a file was uploaded (enqueues ingestion).</summary>
    Task NotifyFileUploadedAsync(
        Guid fileId,
        Guid classroomId,
        string s3Key,
        string fileName,
        string contentType,
        CancellationToken ct = default);

    /// <summary>Notify KnowledgeService that a file was deleted (removes its index entry).</summary>
    Task NotifyFileDeletedAsync(Guid fileId, CancellationToken ct = default);

    /// <summary>
    /// Read a file's indexing status (Pending/Processing/Done/Failed) from KnowledgeService.
    /// Returns <c>null</c> when KnowledgeService has no document registered for the file yet
    /// (its 404), so the caller can present that as still-pending. The internal secret never
    /// leaves this client.
    /// </summary>
    Task<string?> GetIndexingStatusAsync(Guid fileId, CancellationToken ct = default);
}
