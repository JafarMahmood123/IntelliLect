using System.Diagnostics.Metrics;
using StreamingService.Application.Abstractions;

namespace StreamingService.Infrastructure.Observability;

/// <summary>
/// Meter-based capture metrics (R-5). Uses the built-in System.Diagnostics.Metrics so any listener
/// (OpenTelemetry / a Prometheus exporter, wired in ops) can scrape it — no third-party dependency.
/// </summary>
public sealed class RecordingMetrics : IRecordingMetrics, IDisposable
{
    public const string MeterName = "IntelliLect.Streaming.Recordings";

    private readonly Meter _meter;
    private readonly Counter<long> _started;

    public RecordingMetrics()
    {
        _meter = new Meter(MeterName);
        _started = _meter.CreateCounter<long>(
            "recordings_started_total", unit: "{recording}", description: "Room-composite egresses started.");
    }

    public void RecordingStarted() => _started.Add(1);

    public void Dispose() => _meter.Dispose();
}
