namespace ClassroomService.Application.Models;

/// <summary>
/// The grounded answer returned by RagService's RAG pipeline, trimmed to the
/// fields ClassroomService forwards. Deliberately omits RagService internals
/// (chunk ids, similarity scores, s3 keys).
/// </summary>
public sealed record KnowledgeAnswerResult(string Answer, IReadOnlyList<KnowledgeAnswerSource> Sources);

public sealed record KnowledgeAnswerSource(
    int Citation,
    Guid DocumentId,
    int? Page,
    int? Slide,
    string? Section);
