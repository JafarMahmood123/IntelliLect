namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Recording metrics for the ClassroomService side of the flow (R-5): store, download, lifecycle.
/// An interface so it is mockable in tests; the production implementation emits via
/// System.Diagnostics.Metrics.
/// </summary>
public interface IRecordingMetrics
{
    /// <summary>A recording became Available: records the count, its size, and the time from the
    /// egress-complete webhook to Available (recordings_completed_total, recording_size_bytes,
    /// egress_to_available_seconds).</summary>
    void RecordingCompleted(long sizeBytes, double egressToAvailableSeconds);

    /// <summary>A recording ended Failed (recordings_failed_total).</summary>
    void RecordingFailed();

    /// <summary>A pre-signed download URL was issued (download_urls_issued_total).</summary>
    void DownloadUrlIssued();

    /// <summary>A download request was denied by authorization (download_authz_denied_total{reason}).</summary>
    void DownloadAuthzDenied(string reason);

    /// <summary>A recording (object + row) was deleted (recordings_deleted_total).</summary>
    void RecordingDeleted();

    /// <summary>A stuck recording was reconciled (recordings_reconciled_total{outcome}).</summary>
    void RecordingReconciled(string outcome);

    /// <summary>Sets the current count of Processing recordings (recordings_processing_current gauge).</summary>
    void SetProcessingCurrent(int count);
}
