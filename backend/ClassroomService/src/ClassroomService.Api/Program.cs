using ClassroomService.Infrastructure;
using ClassroomService.Application;
using Microsoft.EntityFrameworkCore;
using ClassroomService.Infrastructure.Persistence;
using ClassroomService.Api.Middleware;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication();
builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// R-5: recordings storage (S3 bucket) reachability/config health.
// S-5: summaries download-config health (shares the same bucket; config-only).
builder.Services.AddHealthChecks()
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