using System.Diagnostics.Metrics;
using ClassroomService.Application.Abstractions;

namespace ClassroomService.Infrastructure.Observability;

/// <summary>
/// Meter-based session-summary metrics (S-5) using the built-in System.Diagnostics.Metrics — any
/// listener (OpenTelemetry / a Prometheus exporter, wired in ops) can scrape it, no third-party
/// dependency. Mirrors <see cref="RecordingMetrics"/>.
/// </summary>
public sealed class SummaryMetrics : ISummaryMetrics, IDisposable
{
    public const string MeterName = "IntelliLect.Classroom.Summaries";

    private readonly Meter _meter;
    private readonly Counter<long> _downloadIssued;
    private readonly Counter<long> _authzDenied;

    // Backing value for the observable gauge; bumped as summaries become Available.
    private long _availableCurrent;

    public SummaryMetrics()
    {
        _meter = new Meter(MeterName);

        _downloadIssued = _meter.CreateCounter<long>("summary_download_urls_issued_total", "{url}", "Pre-signed summary download URLs issued.");
        _authzDenied = _meter.CreateCounter<long>("summary_authz_denied_total", "{request}", "Summary requests denied by authorization.");

        _meter.CreateObservableGauge(
            "summaries_available_current",
            () => Interlocked.Read(ref _availableCurrent),
            unit: "{summary}",
            description: "Summaries currently Available.");
    }

    public void DownloadUrlIssued(string format)
        => _downloadIssued.Add(1, new KeyValuePair<string, object?>("format", format));

    public void AuthzDenied(string reason)
        => _authzDenied.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void AvailableIncrement() => Interlocked.Increment(ref _availableCurrent);

    public void Dispose() => _meter.Dispose();
}
