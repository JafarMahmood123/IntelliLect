using ClassroomService.Infrastructure.Observability;
using ClassroomService.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ClassroomService.UnitTests;

/// <summary>
/// The check that makes <c>/health</c> mean something (work-plan §10.1).
///
/// Before this existed, every health check in this solution returned <c>Degraded</c> when its
/// dependency was missing — and <c>MapHealthChecks</c> answers Degraded with <b>200</b>. So
/// <c>/health</c> could not return anything but success, whatever was broken. That is worse than
/// having no endpoint: an orchestrator watching it never restarts the container that needs it,
/// and a smoke suite asserting on it asserts on nothing.
///
/// The distinction under test is therefore not "does it notice a dead database" but
/// "does it report the dead database as <c>Unhealthy</c> rather than Degraded" — the former is
/// the obvious assertion and the latter is the one that decides whether the endpoint works.
/// </summary>
public sealed class DatabaseHealthCheckTests
{
    private static ApplicationDbContext ContextFor(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new ApplicationDbContext(options);
    }

    private static HealthCheckContext EmptyContext() => new()
    {
        Registration = new HealthCheckRegistration(
            "database", _ => null!, HealthStatus.Unhealthy, tags: null),
    };

    [Fact]
    public async Task A_reachable_database_is_healthy()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open(); // an in-memory database exists only while a connection is held open
        await using var context = ContextFor(connection.ConnectionString);

        var result = await new DatabaseHealthCheck(context).CheckHealthAsync(EmptyContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task An_unreachable_database_is_unhealthy_rather_than_degraded()
    {
        // /dev/null is not a directory, so the file can be neither opened nor created.
        await using var context = ContextFor("Data Source=/dev/null/classroom.db");

        var result = await new DatabaseHealthCheck(context).CheckHealthAsync(EmptyContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        // Degraded would be the natural thing to copy from the checks either side of this one,
        // and it would map to 200. A service that cannot reach its own database is not degraded.
        Assert.NotEqual(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task The_failure_description_does_not_leak_where_the_database_lives()
    {
        // /health is unauthenticated. A raw connection error names the host, the database and
        // frequently the user — this endpoint is reachable by anyone who can reach the service.
        await using var context = ContextFor("Data Source=/dev/null/classroom-secret-host.db");

        var result = await new DatabaseHealthCheck(context).CheckHealthAsync(EmptyContext());

        Assert.DoesNotContain("classroom-secret-host", result.Description ?? string.Empty);
        Assert.DoesNotContain("/dev/null", result.Description ?? string.Empty);
    }

    [Fact]
    public async Task The_probe_answers_promptly_rather_than_waiting_out_the_driver()
    {
        // The point of the internal deadline: Npgsql's own connect timeout is 15s, and a
        // /health that blocks that long turns one sick service into a stalled orchestrator —
        // the probe becomes the outage. Sqlite fails fast, so this pins the shape rather than
        // the driver: whatever the provider does, the check returns in well under the 3s cap.
        await using var context = ContextFor("Data Source=/dev/null/classroom.db");
        var check = new DatabaseHealthCheck(context);

        var startedAt = DateTime.UtcNow;
        await check.CheckHealthAsync(EmptyContext());

        Assert.True(
            DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(3),
            "the probe took longer than its own deadline");
    }
}
