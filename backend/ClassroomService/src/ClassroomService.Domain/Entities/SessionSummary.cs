using ClassroomService.Domain.Enums;

namespace ClassroomService.Domain.Entities;

/// <summary>
/// Metadata for a session's generated summary (S-4). Mirrors <see cref="SessionRecording"/>: one row
/// per session, transitioned by the <c>SessionSummaryReadyMessage</c> consumer to
/// <see cref="SummaryStatus.Available"/> or <see cref="SummaryStatus.Failed"/>. The Markdown and PDF
/// live in object storage; the s3 keys are internal and never leave the service.
/// </summary>
public sealed class SessionSummary
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid ClassroomId { get; set; }

    public SummaryStatus Status { get; set; } = SummaryStatus.Generating;

    /// <summary>S3 object key of the Markdown. Null until the summary is Available.</summary>
    public string? MdS3Key { get; set; }

    /// <summary>S3 object key of the PDF rendition. Null until the summary is Available.</summary>
    public string? PdfS3Key { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Set when the summary becomes Available.</summary>
    public DateTime? AvailableAtUtc { get; set; }

    /// <summary>Populated when the summary ends in Failed.</summary>
    public string? Error { get; set; }

    // Navigation
    public Session Session { get; set; } = null!;
}
