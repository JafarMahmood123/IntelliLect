using Microsoft.Extensions.DependencyInjection;
using StreamingService.Application.Abstractions;
using StreamingService.Application.Services;

namespace StreamingService.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IStreamService, StreamService>();
        return services;
    }
}