using System.Diagnostics.Metrics;
using ClassroomService.Application.Abstractions;

namespace ClassroomService.Infrastructure.Observability;

/// <summary>
/// Meter-based recording metrics (R-5) using the built-in System.Diagnostics.Metrics — any listener
/// (OpenTelemetry / a Prometheus exporter, wired in ops) can scrape it, no third-party dependency.
/// </summary>
public sealed class RecordingMetrics : IRecordingMetrics, IDisposable
{
    public const string MeterName = "IntelliLect.Classroom.Recordings";

    private readonly Meter _meter;
    private readonly Counter<long> _completed;
    private readonly Counter<long> _failed;
    private readonly Counter<long> _downloadIssued;
    private readonly Counter<long> _downloadDenied;
    private readonly Counter<long> _deleted;
    private readonly Counter<long> _reconciled;
    private readonly Histogram<long> _sizeBytes;
    private readonly Histogram<double> _egressToAvailable;

    // Backing value for the observable gauge; refreshed by the reconcile job cycle.
    private long _processingCurrent;

    public RecordingMetrics()
    {
        _meter = new Meter(MeterName);

        _completed = _meter.CreateCounter<long>("recordings_completed_total", "{recording}", "Recordings that became Available.");
        _failed = _meter.CreateCounter<long>("recordings_failed_total", "{recording}", "Recordings that ended Failed.");
        _downloadIssued = _meter.CreateCounter<long>("download_urls_issued_total", "{url}", "Pre-signed download URLs issued.");
        _downloadDenied = _meter.CreateCounter<long>("download_authz_denied_total", "{request}", "Download requests denied by authorization.");
        _deleted = _meter.CreateCounter<long>("recordings_deleted_total", "{recording}", "Recordings deleted (object + row).");
        _reconciled = _meter.CreateCounter<long>("recordings_reconciled_total", "{recording}", "Stuck recordings reconciled.");

        _sizeBytes = _meter.CreateHistogram<long>("recording_size_bytes", "By", "Recording file size.");
        _egressToAvailable = _meter.CreateHistogram<double>("egress_to_available_seconds", "s", "Time from egress-complete webhook to Available.");

        _meter.CreateObservableGauge(
            "recordings_processing_current",
            () => Interlocked.Read(ref _processingCurrent),
            unit: "{recording}",
            description: "Recordings currently in Processing.");
    }

    public void RecordingCompleted(long sizeBytes, double egressToAvailableSeconds)
    {
        _completed.Add(1);
        _sizeBytes.Record(sizeBytes);
        if (egressToAvailableSeconds >= 0)
        {
            _egressToAvailable.Record(egressToAvailableSeconds);
        }
    }

    public void RecordingFailed() => _failed.Add(1);

    public void DownloadUrlIssued() => _downloadIssued.Add(1);

    public void DownloadAuthzDenied(string reason)
        => _downloadDenied.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void RecordingDeleted() => _deleted.Add(1);

    public void RecordingReconciled(string outcome)
        => _reconciled.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void SetProcessingCurrent(int count) => Interlocked.Exchange(ref _processingCurrent, count);

    public void Dispose() => _meter.Dispose();
}
