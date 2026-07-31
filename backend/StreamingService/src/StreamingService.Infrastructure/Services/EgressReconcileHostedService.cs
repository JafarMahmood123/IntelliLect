using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamingService.Infrastructure.Configuration;

namespace StreamingService.Infrastructure.Services;

/// <summary>
/// Schedules <see cref="EgressReconciler"/>. Nothing but a timer and a DI scope: the reconciliation
/// itself lives in the reconciler so it can be tested without waiting on ticks.
///
/// Each cycle runs in its own scope; a failing cycle is logged and the loop continues, so a bad
/// pass never takes the host down.
/// </summary>
public sealed class EgressReconcileHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EgressOptions _options;
    private readonly ILogger<EgressReconcileHostedService> _logger;

    public EgressReconcileHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<EgressOptions> options,
        ILogger<EgressReconcileHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.ReconcileIntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        _logger.LogInformation(
            "Egress reconciliation started (interval {Seconds}s).", interval.TotalSeconds);

        // The first tick waits a full interval on purpose: at startup the webhook path is the
        // healthy one, and a freshly-restarted service has no business second-guessing it yet.
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var reconciler = scope.ServiceProvider.GetRequiredService<EgressReconciler>();
                await reconciler.ReconcileAsync(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Egress reconciliation cycle failed; will retry next interval.");
            }
        }
    }
}
