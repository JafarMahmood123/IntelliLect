using ClassroomService.Application.Abstractions;

namespace ClassroomService.Infrastructure.Configuration;

/// <summary>
/// Recording download + lifecycle options, bound from the "Recordings" section. The recording
/// objects live in the same S3 bucket as classroom files, so the existing <c>S3Settings</c>
/// supplies the bucket/credentials; only these knobs are configured here.
/// </summary>
public sealed class RecordingsOptions : IRecordingDownloadSettings, IRecordingLifecycleSettings
{
    public const string SectionName = "Recordings";

    // R-3 — download URL.
    /// <summary>Pre-signed URL lifetime in seconds. Kept short (default 600).</summary>
    public int DownloadUrlTtlSeconds { get; init; } = 600;

    // R-4 — reconcile.
    /// <summary>A Processing recording older than this is "stuck" (default 30).</summary>
    public int StuckProcessingMinutes { get; init; } = 30;

    /// <summary>Whether the reconcile background pass runs (default true).</summary>
    public bool ReconcileEnabled { get; init; } = true;

    /// <summary>How often the background job runs, in minutes (default 15).</summary>
    public int ReconcileIntervalMinutes { get; init; } = 15;

    // R-4 — retention (off by default).
    /// <summary>Auto-delete recordings older than this many days. 0 = keep forever (default 0).</summary>
    public int RetentionDays { get; init; }

    /// <summary>Whether retention auto-deletion runs (default false).</summary>
    public bool RetentionEnabled { get; init; }
}
