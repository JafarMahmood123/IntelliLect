namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Safety net for sessions the teacher never closed — a crashed browser, a lost connection, or a
/// teacher who simply walked away. Without it such a session stays Live forever: students can
/// still join, the recording egress keeps running and no summary is ever produced.
/// </summary>
public interface IStalledSessionSweeper
{
    /// <summary>Ends every session that has been live past the configured threshold.</summary>
    /// <returns>How many sessions this pass closed.</returns>
    Task<int> SweepAsync(CancellationToken ct = default);
}

/// <summary>Tunables for <see cref="IStalledSessionSweeper"/>, bound from the "Sessions" section.</summary>
public interface IStalledSessionSettings
{
    /// <summary>A session live for at least this long is considered stalled.</summary>
    int StalledAfterHours { get; }

    /// <summary>Maximum sessions closed per pass, so one cycle cannot run unbounded.</summary>
    int StalledSweepBatchSize { get; }
}
