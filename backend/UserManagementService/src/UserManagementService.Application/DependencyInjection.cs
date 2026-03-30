using Microsoft.Extensions.DependencyInjection;
using UserManagementService.Application.Authentication;

namespace UserManagementService.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IAuthService, AuthService>();
        return services;
    }
}