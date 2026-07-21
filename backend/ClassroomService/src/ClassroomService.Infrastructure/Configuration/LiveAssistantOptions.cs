namespace ClassroomService.Infrastructure.Configuration;

/// <summary>
/// Options for calling LiveAssistantService's internal transcript endpoints. Bound from the
/// "LiveAssistant" configuration section (mirrors <see cref="KnowledgeServiceOptions"/>).
/// </summary>
public class LiveAssistantOptions
{
    public const string SectionName = "LiveAssistant";

    /// <summary>Base URL, e.g. http://live-assistant-service:8080 (compose service DNS).</summary>
    public string BaseUrl { get; init; } = null!;

    /// <summary>Shared secret matching LiveAssistantService's INTERNAL_API_SECRET.</summary>
    public string InternalApiSecret { get; init; } = string.Empty;

    /// <summary>Per-request HTTP timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 10;
}
