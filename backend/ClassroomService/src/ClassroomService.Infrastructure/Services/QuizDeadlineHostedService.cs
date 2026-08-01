using ClassroomService.Application.Abstractions;
using ClassroomService.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassroomService.Infrastructure.Services;

/// <summary>
/// Closes timed-out quizzes on a short cadence. Mirrors <see cref="StalledSessionSweepHostedService"/>:
/// a DI scope per cycle, and a failing cycle logged and skipped so the loop never crashes the host.
///
/// The cadence is SECONDS, not minutes, because this one is watched by a room. A class that has
/// just run out of time is waiting to see its marks, and a minute of "time up, nothing happening"
/// reads as a broken quiz.
/// </summary>
public sealed class QuizDeadlineHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly QuizOptions _options;
    private readonly ILogger<QuizDeadlineHostedService> _logger;

    public QuizDeadlineHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<QuizOptions> options,
        ILogger<QuizDeadlineHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = Math.Clamp(_options.DeadlineSweepSeconds, 1, 300);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        _logger.LogInformation("Quiz deadline sweep started (every {Seconds}s).", intervalSeconds);

        do
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var sweeper = scope.ServiceProvider.GetRequiredService<IQuizDeadlineSweeper>();
                await sweeper.SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Quiz deadline sweep cycle failed; will retry next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
