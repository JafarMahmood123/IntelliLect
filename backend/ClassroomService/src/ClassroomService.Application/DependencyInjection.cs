using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace ClassroomService.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // AutoMapper 14 changed this overload: the assembly list moved onto the configuration
        // expression, and the (Assembly[]) form no longer exists.
        services.AddAutoMapper(cfg => cfg.AddMaps(Assembly.GetExecutingAssembly()));

        return services;
    }
}
