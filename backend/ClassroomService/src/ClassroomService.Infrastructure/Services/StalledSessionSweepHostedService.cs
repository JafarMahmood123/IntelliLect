using ClassroomService.Application.Abstractions;
using ClassroomService.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassroomService.Infrastructure.Services;

/// <summary>
/// Runs the stalled-session sweep on a fixed cadence (hourly by default). Mirrors
/// <see cref="RecordingReconcileHostedService"/>: each cycle gets its own DI scope, and a failing
/// cycle is logged and skipped so the loop never crashes the host.
/// Safe to run on several instances — the Live -> Ended transition is claimed atomically in the
/// database, so only one instance actually tears a given session down.
/// </summary>
public sealed class StalledSessionSweepHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SessionsOptions _options;
    private readonly ILogger<StalledSessionSweepHostedService> _logger;

    public StalledSessionSweepHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<SessionsOptions> options,
        ILogger<StalledSessionSweepHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = Math.Max(1, _options.StalledSweepIntervalMinutes);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

        _logger.LogInformation(
            "Stalled-session sweep started (every {Minutes}m, stalled after {Hours}h).",
            intervalMinutes, _options.StalledAfterHours);

        do
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var sweeper = scope.ServiceProvider.GetRequiredService<IStalledSessionSweeper>();
                await sweeper.SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stalled-session sweep cycle failed; will retry next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
