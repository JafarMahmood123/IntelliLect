namespace ClassroomService.Application.DTOs.File;

/// <summary>
/// A classroom file's RAG indexing status, safe to return to classroom members.
/// Status is one of Pending | Processing | Done | Failed. Exposes no KnowledgeService
/// internals (no s3 keys, error detail, or internal secret).
/// </summary>
public record FileIndexingStatusResponse(Guid FileId, string Status);
