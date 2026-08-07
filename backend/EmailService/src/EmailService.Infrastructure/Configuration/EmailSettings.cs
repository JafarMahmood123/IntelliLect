using System.ComponentModel.DataAnnotations;

namespace EmailService.Infrastructure.Configuration;

/// <summary>
/// How this service reaches the SMTP server, bound and validated at STARTUP.
///
/// These five values used to be read by string index on every single send, with a default beside
/// each read. That arrangement had three separate ways of failing quietly, and the last of them
/// disabled the guard that was written to prevent the first:
///
///   * <c>appsettings.json</c> ships <c>"SenderEmail": ""</c> and <c>"AppPassword": ""</c>. The
///     sender guarded with <c>settings["AppPassword"] ?? throw</c> — and an empty string is not
///     null, so the guard never fired. The service booted, bound all five queues, and answered
///     <c>/health</c> with "ok" while authenticating to Gmail with a blank password. Every email
///     it was asked to send failed, retried three times, and went to the error queue: the reset
///     code nobody received, the approval nobody was told about.
///   * A malformed port fell back to 587 via <c>int.TryParse(...) ? parsed : 587</c>. Setting
///     <c>SmtpPort=465</c> with a stray character does not get you 465 and does not get you an
///     error; it gets you 587, and a STARTTLS negotiation against a server expecting TLS from the
///     first byte.
///   * The transport security was not configurable at all, so a provider on 465 (implicit TLS)
///     could not be used regardless of what the port said.
///
/// Every other service in the platform already binds its settings this way — UMS's
/// <c>InternalServiceOptions</c>, ClassroomService's <c>S3Settings</c>. This is the last one that
/// did not, and it is the one holding a credential.
/// </summary>
public sealed class EmailSettings
{
    public const string SectionName = "EmailSettings";

    /// <summary>Display name on the From header. The only value here with a safe default.</summary>
    public string SenderName { get; init; } = "IntelliLect";

    /// <summary>
    /// The mailbox mail is sent from, and the SMTP username — Gmail authenticates as the sender.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [EmailAddress]
    public string SenderEmail { get; init; } = null!;

    /// <summary>
    /// The SMTP password (a Gmail app password in this deployment).
    ///
    /// <c>AllowEmptyStrings = false</c> is the point of the attribute here, not <c>Required</c>
    /// itself: the value that broke this was present and blank, not absent.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string AppPassword { get; init; } = null!;

    [Required(AllowEmptyStrings = false)]
    public string SmtpHost { get; init; } = "smtp.gmail.com";

    /// <summary>
    /// Range-checked so a typo is a refusal to start rather than a silent 587. Binding an
    /// <c>int</c> also means a non-numeric value fails at startup naming the key, which is what
    /// <c>TryParse</c>-with-a-fallback was quietly preventing.
    /// </summary>
    [Range(1, 65535)]
    public int SmtpPort { get; init; } = 587;

    /// <summary>
    /// How TLS is established. Configurable, but only between the two options that actually
    /// encrypt — see <see cref="SmtpSecurity"/>.
    /// </summary>
    public SmtpSecurity Security { get; init; } = SmtpSecurity.StartTls;

    /// <summary>
    /// Per-send timeout.
    ///
    /// MailKit's default is 120 seconds, and nothing here overrode it. With five consumers on a
    /// three-attempt retry policy, one black-holed SMTP host held a consumer for six minutes per
    /// message — long enough for the queue to back up behind mail that was never going to send.
    /// </summary>
    [Range(1, 300)]
    public int TimeoutSeconds { get; init; } = 30;
}

/// <summary>
/// The transport-security choices this service will accept.
///
/// Deliberately not the whole of MailKit's <c>SecureSocketOptions</c>. That enum also offers
/// <c>None</c> and the two <c>WhenAvailable</c> variants, and every one of them can put this
/// service's password on the wire in the clear — <c>StartTlsWhenAvailable</c> most dangerously of
/// all, because it does so only when something upstream has gone wrong, which is exactly when
/// nobody is reading the logs. A setting that can be misconfigured into sending a credential
/// unencrypted is not a setting worth having.
/// </summary>
public enum SmtpSecurity
{
    /// <summary>Connect in the clear, then upgrade with STARTTLS before authenticating. Port 587.</summary>
    StartTls,

    /// <summary>TLS from the first byte, no plaintext phase at all. Port 465.</summary>
    SslOnConnect,
}
