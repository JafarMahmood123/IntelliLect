using ClassroomService.Infrastructure;
using ClassroomService.Application;
using Microsoft.EntityFrameworkCore;
using ClassroomService.Infrastructure.Persistence;
using ClassroomService.Api.Middleware;
using ClassroomService.Presentation.Filters;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication();
builder.Services.AddControllers();

// Registered here rather than in Infrastructure because the filter lives in Presentation, and
// Infrastructure does not reference it. [ServiceFilter] resolves it from the container so it can
// take IUploadSettings — an attribute alone could not, the limit being configuration.
builder.Services.AddScoped<UploadSizeLimitFilter>();
builder.Services.AddOpenApi();

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// R-5: recordings storage (S3 bucket) reachability/config health.
// S-5: summaries download-config health (shares the same bucket; config-only).
// §10.1: the database check is the one that can return Unhealthy, and therefore the only
// one that makes /health a probe rather than a formality. The two below report Degraded,
// which MapHealthChecks answers with 200.
builder.Services.AddHealthChecks()
    .AddCheck<ClassroomService.Infrastructure.Observability.DatabaseHealthCheck>(
        "database", tags: new[] { "liveness" })
    .AddCheck<ClassroomService.Api.HealthChecks.RecordingsStorageHealthCheck>(
        "recordings_storage", tags: new[] { "recording" })
    .AddCheck<ClassroomService.Api.HealthChecks.SummariesConfigHealthCheck>(
        "summaries-config", tags: new[] { "summary" });

var app = builder.Build();

app.UseExceptionHandler();

using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
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
app.MapHealthChecks("/health");

app.Run();