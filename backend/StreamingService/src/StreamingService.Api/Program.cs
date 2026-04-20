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

    var app = builder.Build();
    app.UseSerilogRequestLogging();

    using (var scope = app.Services.CreateScope())
    {
        var context = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        context.Database.Migrate();
    }

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.UseHttpsRedirection();

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();
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