using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UserManagementService.Application.Abstractions;
using UserManagementService.Infrastructure.Authentication;
using UserManagementService.Infrastructure.Hashing;
using UserManagementService.Infrastructure.Persistence;
using UserManagementService.Infrastructure.Persistence.Repositories;

namespace UserManagementService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, 
        IConfiguration configuration)
    {
        // 1. Database Configuration
        var connectionString = configuration.GetConnectionString("Database");
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString));

        // 2. Authentication & Hashing
        services.AddSingleton<IHasher, Hasher>();
        
        var jwtSettings = configuration.GetSection("Jwt");
        services.AddSingleton<IJwtProvider>(_ => 
            new JwtProvider(
                jwtSettings["SecretKey"]!, 
                jwtSettings["Issuer"]!, 
                jwtSettings["Audience"]!));

        // 3. Repositories
        services.AddScoped<IUserRepository, UserRepository>();
        
        // This line is the "magic" that fixes the IRepository<Role> error:
        services.AddScoped(typeof(IRepository<>), typeof(GenericRepository<>));

        return services;
    }
}