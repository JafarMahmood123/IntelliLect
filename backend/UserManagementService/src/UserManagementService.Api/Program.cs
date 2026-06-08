using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Serilog;
using UserManagementService.Api.Middleware;
using UserManagementService.Application;
using UserManagementService.Domain.Entities;
using UserManagementService.Infrastructure;
using UserManagementService.Infrastructure.Persistence;
using UserManagementService.Infrastructure.Persistence.Seeder;
using UserManagementService.Presentation;

JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();


Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting web application");
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services));

    builder.Services
        .AddApplication()
        .AddInfrastructure(builder.Configuration)
        .AddPresentation();

    builder.Services.AddOpenApi();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddProblemDetails();

    var app = builder.Build();

    using (var scope = app.Services.CreateScope())
    {
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<ApplicationDbContext>();
        var logger = services.GetRequiredService<ILogger<Program>>();

        // Retry policy: Try 5 times with a 2-second gap
        int retries = 5;
        while (retries > 0)
        {
            try
            {
                logger.LogInformation("Attempting to apply migrations... (Retries left: {Retries})", retries);
                context.Database.Migrate();

                await DatabaseSeeder.SeedAsync(services); 

                break;
            }
            catch (Exception ex)
            {
                retries--;
                if (retries == 0)
                {
                    logger.LogCritical(ex, "Could not apply migrations after multiple attempts. Shutting down.");
                    throw; // Crash the app so Docker restarts it
                }
                logger.LogWarning("Database not ready yet. Retrying in 2 seconds...");
                Thread.Sleep(2000);
            }
        }
    }

    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    });

    app.UseSerilogRequestLogging();
    app.UseExceptionHandler();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference();
    }

    app.UseHttpsRedirection();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
