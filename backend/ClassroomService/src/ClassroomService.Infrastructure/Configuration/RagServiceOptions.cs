namespace ClassroomService.Infrastructure.Configuration;

/// <summary>
/// Options for calling RagService's internal ingestion endpoints. Bound from
/// the "RagService" configuration section (mirrors <see cref="S3Settings"/>).
/// </summary>
public class RagServiceOptions
{
    public const string SectionName = "RagService";

    /// <summary>Base URL, e.g. http://rag-service:8080 (compose service DNS).</summary>
    public string BaseUrl { get; init; } = null!;

    /// <summary>Shared secret matching RagService's INTERNAL_API_SECRET.</summary>
    public string InternalApiSecret { get; init; } = string.Empty;

    /// <summary>Per-request HTTP timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 10;
}
