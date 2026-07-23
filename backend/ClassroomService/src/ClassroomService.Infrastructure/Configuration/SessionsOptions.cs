using ClassroomService.Application.Abstractions;

namespace ClassroomService.Infrastructure.Configuration;

/// <summary>
/// Session-lifecycle options, bound from the "Sessions" section. Only the stalled-session safety
/// net is configured here; ending a session on request needs no configuration.
/// </summary>
public sealed class SessionsOptions : IStalledSessionSettings
{
    public const string SectionName = "Sessions";

    /// <summary>Whether the periodic stalled-session sweep runs (default true).</summary>
    public bool StalledSweepEnabled { get; init; } = true;

    /// <summary>How often the sweep runs, in minutes (default 60 — hourly).</summary>
    public int StalledSweepIntervalMinutes { get; init; } = 60;

    /// <summary>A session live for at least this many hours is stalled (default 4).</summary>
    public int StalledAfterHours { get; init; } = 4;

    /// <summary>Maximum sessions closed per pass (default 50).</summary>
    public int StalledSweepBatchSize { get; init; } = 50;
}
