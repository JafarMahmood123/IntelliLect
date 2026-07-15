using ClassroomService.Application.Abstractions;
using ClassroomService.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassroomService.Infrastructure.Services;

/// <summary>
/// Periodic recording maintenance (R-4): reconciles stuck Processing recordings and applies
/// retention. Registered only when reconcile or retention is enabled. Each cycle runs in its own
/// DI scope; a failing cycle is logged and the loop continues (never crashes the host).
/// </summary>
public sealed class RecordingReconcileHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RecordingsOptions _options;
    private readonly ILogger<RecordingReconcileHostedService> _logger;

    public RecordingReconcileHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<RecordingsOptions> options,
        ILogger<RecordingReconcileHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = Math.Max(1, _options.ReconcileIntervalMinutes);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

        _logger.LogInformation(
            "Recording maintenance started (interval {Minutes}m, reconcile={Reconcile}, retention={Retention}).",
            intervalMinutes, _options.ReconcileEnabled, _options.RetentionEnabled);

        do
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Recording maintenance cycle failed; will retry next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IRecordingLifecycleService>();

        if (_options.ReconcileEnabled)
        {
            await lifecycle.ReconcileStuckProcessingAsync(ct);
        }

        // No-op internally when retention is disabled.
        await lifecycle.ApplyRetentionAsync(ct);
    }
}
