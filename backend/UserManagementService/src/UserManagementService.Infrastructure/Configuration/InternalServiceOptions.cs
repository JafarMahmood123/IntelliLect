using System.ComponentModel.DataAnnotations;

namespace UserManagementService.Infrastructure.Configuration;

/// <summary>
/// How this service reaches one downstream service's <c>/api/internal</c> routes.
///
/// UMS talks to four of them and every one had the same three settings read by string index,
/// scattered between the composition root and each client — with an inline default per read, so
/// the real value of any of them was not discoverable from one place. Every other service in the
/// platform already binds this shape (ClassroomService's <c>RagServiceOptions</c>,
/// StreamingService's <c>LiveKitSettings</c>); this is the last one that did not.
///
/// Validated at startup rather than at first use. The failure mode being closed here is a
/// deployment that comes up healthy and only misbehaves when a super admin opens a page: the
/// panels are empty, every internal call has 401'd, and nothing says the secret was never set.
/// </summary>
public abstract class InternalServiceOptions
{
    /// <summary>Base URL, e.g. http://classroom-service:8080 (compose service DNS).</summary>
    [Required(AllowEmptyStrings = false)]
    [Url]
    public string BaseUrl { get; init; } = null!;

    /// <summary>
    /// Shared secret matching the downstream service's INTERNAL_API_SECRET.
    ///
    /// Required, because these routes fail closed on the far side: without it every call is a 401
    /// that reads as "the other service is down". Every compose file already sets it from a single
    /// <c>INTERNAL_API_SECRET</c> with <c>:?</c>, so requiring it here changes nothing that runs
    /// today — it only stops a hand-rolled deployment starting half-wired.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string InternalApiSecret { get; init; } = null!;

    /// <summary>
    /// Per-request HTTP timeout in seconds.
    ///
    /// Range-checked because <see cref="HttpClient.Timeout"/> throws on zero or a negative value —
    /// so a typo'd <c>0</c> used to take the service down at startup with an
    /// <c>ArgumentOutOfRangeException</c> that names neither the setting nor the service it
    /// belongs to.
    /// </summary>
    [Range(1, 600)]
    public int TimeoutSeconds { get; init; } = 10;
}

/// <summary>ClassroomService — the super-admin user-detail view.</summary>
public sealed class ClassroomServiceOptions : InternalServiceOptions
{
    public const string SectionName = "ClassroomService";
}

/// <summary>
/// StreamingService — live stream snapshots for the session monitor.
///
/// Its timeout was hard-coded to 5s while compose supplied a <c>TimeoutSeconds</c> that nothing
/// read: a setting that is provided and ignored is worse than either value, because the next
/// person to change it will believe they have. The 5s now lives in the compose files, where it
/// is visible and adjustable, and this binds it like every other.
/// </summary>
public sealed class StreamingServiceOptions : InternalServiceOptions
{
    public const string SectionName = "StreamingService";
}

/// <summary>LiveAssistantService — active session ids for the session monitor. Same story.</summary>
public sealed class LiveAssistantOptions : InternalServiceOptions
{
    public const string SectionName = "LiveAssistant";
}

/// <summary>RagService — super-admin knowledge-base management.</summary>
public sealed class RagServiceOptions : InternalServiceOptions
{
    public const string SectionName = "RagService";
}
