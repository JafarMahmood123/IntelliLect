namespace StreamingService.Infrastructure.Configuration;

/// <summary>
/// How this service reaches ClassroomService's <c>/api/internal</c> routes, to ask whether a user
/// belongs to a classroom. Bound from the "ClassroomService" section; mirrors
/// <see cref="LiveAssistantOptions"/>.
/// </summary>
public class ClassroomServiceOptions
{
    public const string SectionName = "ClassroomService";

    /// <summary>Base URL, e.g. http://classroom-service:8080 (compose service DNS).</summary>
    public string BaseUrl { get; init; } = null!;

    /// <summary>Shared secret matching ClassroomService's <c>InternalApi:Secret</c>.</summary>
    public string InternalApiSecret { get; init; } = string.Empty;

    /// <summary>
    /// Per-request HTTP timeout in seconds. Three, not the five the assistant client uses: this one
    /// sits in front of a student pressing "join", so the cost of waiting is a lecture they are not
    /// yet in. A timeout refuses the token, which is the safe answer and a retriable one.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 3;
}
