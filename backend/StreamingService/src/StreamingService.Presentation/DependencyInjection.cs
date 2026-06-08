using Microsoft.Extensions.DependencyInjection;
using StreamingService.Application.Abstractions;
using StreamingService.Presentation.Services;

namespace StreamingService.Presentation;

public static class DependencyInjection
{
    public static IServiceCollection AddPresentation(this IServiceCollection services)
    {
        services.AddControllers();

        services.AddSignalR();

        services.AddScoped<IStreamHubContext, StreamHubContext>();

        return services;
    }
}