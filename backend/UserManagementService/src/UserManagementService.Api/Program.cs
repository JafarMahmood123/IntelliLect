using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Serilog;
using UserManagementService.Api.Middleware;
using UserManagementService.Application;
using UserManagementService.Domain.Entities;
using UserManagementService.Infrastructure;
using UserManagementService.Infrastructure.Persistence;
using UserManagementService.Presentation;

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

        try
        {
            logger.LogInformation("Applying migrations...");
            context.Database.Migrate();

            if (!context.Roles.Any())
            {
                logger.LogInformation("Seeding database with roles...");
                context.Roles.AddRange(
                    Role.Create(RoleName.Admin),
                    Role.Create(RoleName.Teacher),
                    Role.Create(RoleName.Student)
                );
                context.SaveChanges();
                logger.LogInformation("Database seeded successfully.");
            }
            else
            {
                logger.LogInformation("Database already has roles, skipping seed.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred during migration or seeding.");
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