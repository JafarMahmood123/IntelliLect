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
}
