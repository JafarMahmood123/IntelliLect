using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ClassroomService.Infrastructure.Persistence;

namespace ClassroomService.Infrastructure.Observability;

/// <summary>
/// Liveness that can actually fail (work-plan §10.1).
///
/// Every other health check in this solution reports <c>Degraded</c> when its dependency is
/// missing, and <c>MapHealthChecks</c> maps Degraded to <b>200</b> — so before this existed,
/// <c>/health</c> could not return anything but success no matter what was broken. An endpoint
/// that cannot fail is not a probe: an orchestrator watching it would never restart the one
/// container that needed it, and a smoke suite asserting on it would be asserting on nothing.
///
/// The database is the right thing to gate liveness on, because it is the dependency whose
/// absence makes this service unable to serve any request at all. Degraded is deliberately not
/// used here: a service that cannot reach its own database is not degraded, it is down.
/// </summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    /// <summary>
    /// A probe must answer faster than the thing it is probing gives up. Npgsql's own connect
    /// timeout is 15s, and a <c>/health</c> that blocks for 15s turns one sick service into a
    /// stalled orchestrator — the probe becomes the outage.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly ApplicationDbContext _dbContext;

    public DatabaseHealthCheck(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            return await _dbContext.Database.CanConnectAsync(timeout.Token)
                ? HealthCheckResult.Healthy("Database reachable.")
                : HealthCheckResult.Unhealthy("Database unreachable.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy(
                $"Database did not answer within {ProbeTimeout.TotalSeconds:0}s.");
        }
        catch (Exception ex)
        {
            // The message is deliberately generic: /health is unauthenticated, and a raw
            // connection error names the host, the database and often the user.
            return HealthCheckResult.Unhealthy("Database unreachable.", ex);
        }
    }
}
