namespace StreamingService.Application.Abstractions;

/// <summary>
/// Capture-side recording metrics (R-5). An interface so it is mockable in tests and has a
/// no-op-friendly default; the production implementation emits via System.Diagnostics.Metrics.
/// </summary>
public interface IRecordingMetrics
{
    /// <summary>A room-composite egress was successfully started (recordings_started_total).</summary>
    void RecordingStarted();
}
