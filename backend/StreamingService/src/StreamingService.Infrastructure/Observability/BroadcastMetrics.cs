using System.Diagnostics.Metrics;
using StreamingService.Application.Abstractions;

namespace StreamingService.Infrastructure.Observability;

/// <summary>
/// Meter-based SignalR fan-out metrics (§9.2). Built-in <c>System.Diagnostics.Metrics</c>, matching
/// <see cref="RecordingMetrics"/>, so any listener (OpenTelemetry / a Prometheus exporter, wired in
/// ops) can scrape it without this service taking a dependency on one.
/// </summary>
public sealed class BroadcastMetrics : IBroadcastMetrics, IDisposable
{
    public const string MeterName = "IntelliLect.Streaming.Broadcast";
    public const string DurationInstrument = "signalr_broadcast_duration_seconds";

    private readonly Meter _meter;
    private readonly Histogram<double> _duration;

    public BroadcastMetrics()
    {
        _meter = new Meter(MeterName);
        _duration = _meter.CreateHistogram<double>(
            DurationInstrument,
            unit: "s",
            description: "Time for the hub to fan one event out to every connection in a session group.");
    }

    public void BroadcastCompleted(string eventName, double seconds)
        => _duration.Record(seconds, new KeyValuePair<string, object?>("event", eventName));

    public void Dispose() => _meter.Dispose();
}
