namespace StreamingService.Infrastructure.Configuration;

/// <summary>
/// Options for notifying the (Python) LiveAssistantService about session start/stop so
/// it can run/tear down the teaching-assistant agent. Bound from the "LiveAssistant"
/// configuration section. Mirrors ClassroomService's KnowledgeServiceOptions.
/// The assistant is an enhancement, not a dependency — see LiveAssistantInternalClient.
/// </summary>
public class LiveAssistantOptions
{
    public const string SectionName = "LiveAssistant";

    /// <summary>Base URL, e.g. http://live-assistant-service:8080 (compose service DNS).</summary>
    public string BaseUrl { get; init; } = null!;

    /// <summary>Shared secret matching LiveAssistantService's INTERNAL_API_SECRET.</summary>
    public string InternalApiSecret { get; init; } = string.Empty;

    /// <summary>Per-request HTTP timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 5;
}
