using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Authentication;

namespace UserManagementService.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IManagementService, ManagementService>();
        services.AddAutoMapper(Assembly.GetExecutingAssembly());

        return services;
    }
}