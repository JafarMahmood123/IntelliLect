namespace ClassroomService.Infrastructure.Configuration;

/// <summary>
/// Options for calling StreamingService's internal endpoints. Bound from the "StreamingService"
/// configuration section (mirrors <see cref="RagServiceOptions"/> and
/// <see cref="LiveAssistantOptions"/>).
///
/// Added late: both streaming clients previously wrote
/// <c>http://streaming-service:8080/...</c> into the call itself, making this the one internal
/// hop in the service that could not be pointed anywhere else — no override for a different
/// compose project, a host-run service, or a test host.
/// </summary>
public class StreamingServiceOptions
{
    public const string SectionName = "StreamingService";

    /// <summary>Base URL, e.g. http://streaming-service:8080 (compose service DNS).</summary>
    public string BaseUrl { get; init; } = null!;

    /// <summary>Per-request HTTP timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// Shared secret presented as <c>X-Internal-Secret</c> on every call to StreamingService's
    /// internal routes. Must match that service's <c>Internal:ApiSecret</c>; its guard fails
    /// closed, so an unset value here means every call is refused rather than silently allowed.
    /// </summary>
    public string InternalApiSecret { get; init; } = string.Empty;
}
