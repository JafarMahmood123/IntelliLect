namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Session-summary metrics for the ClassroomService read side (S-5): download-URL issuance,
/// authorization denials, and the count of available summaries. Mirrors
/// <see cref="IRecordingMetrics"/>. An interface so it is mockable in tests; the production
/// implementation emits via System.Diagnostics.Metrics.
/// </summary>
public interface ISummaryMetrics
{
    /// <summary>A pre-signed summary download URL was issued for the given artifact format
    /// ("pdf" or "md") (summary_download_urls_issued_total{format}).</summary>
    void DownloadUrlIssued(string format);

    /// <summary>A summary request was denied by authorization
    /// (summary_authz_denied_total{reason}).</summary>
    void AuthzDenied(string reason);

    /// <summary>A summary became Available: bumps the current count of available summaries
    /// (summaries_available_current gauge).</summary>
    void AvailableIncrement();
}
