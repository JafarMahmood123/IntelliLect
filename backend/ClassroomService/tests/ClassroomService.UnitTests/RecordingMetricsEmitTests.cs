using System.Diagnostics.Metrics;
using ClassroomService.Infrastructure.Observability;

namespace ClassroomService.UnitTests;

/// <summary>
/// Verifies the production Meter-based metrics actually emit measurements (so a scraper/exporter
/// wired in ops would see them), not just that the mockable interface is called.
/// </summary>
public sealed class RecordingMetricsEmitTests
{
    [Fact]
    public void Concrete_metrics_emit_measurements_on_the_recordings_meter()
    {
        var longMeasurements = new List<(string Name, long Value)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == RecordingMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((inst, val, _, _) => longMeasurements.Add((inst.Name, val)));
        listener.Start();

        // Instruments are created in the ctor -> InstrumentPublished fires and enables them.
        using var metrics = new RecordingMetrics();
        metrics.RecordingCompleted(sizeBytes: 2048, egressToAvailableSeconds: 3.5);
        metrics.RecordingDeleted();
        metrics.RecordingReconciled("failed");

        Assert.Contains(longMeasurements, m => m.Name == "recordings_completed_total" && m.Value == 1);
        Assert.Contains(longMeasurements, m => m.Name == "recording_size_bytes" && m.Value == 2048);
        Assert.Contains(longMeasurements, m => m.Name == "recordings_deleted_total" && m.Value == 1);
        Assert.Contains(longMeasurements, m => m.Name == "recordings_reconciled_total" && m.Value == 1);
    }
}
