using Microsoft.EntityFrameworkCore;
using Serilog;
using StreamingService.Api.Middleware;
using StreamingService.Application;
using StreamingService.Infrastructure;
using StreamingService.Infrastructure.Persistence;
using StreamingService.Presentation;
using StreamingService.Presentation.Hubs;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting Streaming Service");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services));

    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddPresentation();
    builder.Services.AddApplication();
    builder.Services.AddOpenApi();

    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddProblemDetails();

    // §10.1: the database check is the one that can return Unhealthy, and therefore the only
    // one that makes /health a probe rather than a formality. The egress check below reports
    // Degraded, which MapHealthChecks answers with 200.
    // R-5: recording capture config health (LiveKit egress + webhook verification key).
    builder.Services.AddHealthChecks()
        .AddCheck<StreamingService.Infrastructure.Observability.DatabaseHealthCheck>(
            "database", tags: new[] { "liveness" })
        .AddCheck<StreamingService.Api.HealthChecks.EgressConfigHealthCheck>(
            "recording_capture_config", tags: new[] { "recording" });

    var app = builder.Build();

    using (var scope = app.Services.CreateScope())
    {
        var context = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

        try
        {
            logger.LogInformation("Applying database migrations...");
            context.Database.Migrate();
            logger.LogInformation("Database migrations applied successfully.");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Failed to apply database migrations.");
            throw;
        }
    }

    app.UseExceptionHandler();
    app.UseSerilogRequestLogging();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.UseHttpsRedirection();

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();
    app.MapHealthChecks("/health");
    app.MapHub<StreamHub>("/hubs/stream");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Streaming Service terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
