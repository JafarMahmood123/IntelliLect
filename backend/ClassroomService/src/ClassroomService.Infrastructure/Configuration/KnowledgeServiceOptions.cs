namespace ClassroomService.Infrastructure.Configuration;

/// <summary>
/// Options for calling KnowledgeService's internal ingestion endpoints. Bound from
/// the "KnowledgeService" configuration section (mirrors <see cref="S3Settings"/>).
/// </summary>
public class KnowledgeServiceOptions
{
    public const string SectionName = "KnowledgeService";

    /// <summary>Base URL, e.g. http://knowledge-service:8080 (compose service DNS).</summary>
    public string BaseUrl { get; init; } = null!;

    /// <summary>Shared secret matching KnowledgeService's INTERNAL_API_SECRET.</summary>
    public string InternalApiSecret { get; init; } = string.Empty;

    /// <summary>Per-request HTTP timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 10;
}
