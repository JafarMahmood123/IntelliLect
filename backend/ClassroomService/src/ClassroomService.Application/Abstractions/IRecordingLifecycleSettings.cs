namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Lifecycle/retention tuning the application layer needs, surfaced as an interface so the service
/// does not depend on IOptions/Infrastructure. Bound from the "Recordings" config section.
/// </summary>
public interface IRecordingLifecycleSettings
{
    /// <summary>A Processing recording older than this (minutes) is considered stuck.</summary>
    int StuckProcessingMinutes { get; }

    /// <summary>When true and <see cref="RetentionDays"/> &gt; 0, recordings older than the cutoff
    /// are auto-deleted. Default off — nothing is auto-deleted unless explicitly enabled.</summary>
    bool RetentionEnabled { get; }

    /// <summary>Age (days) after which a recording is auto-deleted. 0 = keep forever.</summary>
    int RetentionDays { get; }
}
