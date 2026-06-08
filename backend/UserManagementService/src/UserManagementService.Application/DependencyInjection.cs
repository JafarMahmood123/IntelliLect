using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Authentication;
using UserManagementService.Application.SuperAdministration;

namespace UserManagementService.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IManagementService, ManagementService>();
        services.AddScoped<ISuperAdminService, SuperAdminService>();
        services.AddAutoMapper(Assembly.GetExecutingAssembly());

        return services;
    }
}
