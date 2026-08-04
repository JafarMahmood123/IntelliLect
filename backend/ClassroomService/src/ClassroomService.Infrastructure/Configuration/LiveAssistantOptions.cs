namespace ClassroomService.Infrastructure.Configuration;

/// <summary>
/// Options for calling LiveAssistantService's internal transcript endpoints. Bound from the
/// "LiveAssistant" configuration section (mirrors <see cref="RagServiceOptions"/>).
/// </summary>
public class LiveAssistantOptions
{
    public const string SectionName = "LiveAssistant";

    /// <summary>Base URL, e.g. http://live-assistant-service:8080 (compose service DNS).</summary>
    public string BaseUrl { get; init; } = null!;

    /// <summary>Shared secret matching LiveAssistantService's INTERNAL_API_SECRET.</summary>
    public string InternalApiSecret { get; init; } = string.Empty;

    /// <summary>Per-request HTTP timeout in seconds, for the quick transcript calls.</summary>
    public int TimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// Timeout for quiz generation, which runs a language model over the session transcript and
    /// legitimately takes tens of seconds — far longer than a transcript lookup should ever be
    /// allowed to hang. Kept separate so waiting for a model does not also mean waiting two
    /// minutes to notice that a transcript delete has stalled.
    /// </summary>
    public int GenerationTimeoutSeconds { get; init; } = 120;
}
