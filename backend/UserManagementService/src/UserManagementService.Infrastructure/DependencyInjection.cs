using System.Text;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using UserManagementService.Application.Abstractions;
using UserManagementService.Infrastructure.Authentication;
using UserManagementService.Infrastructure.BackgroundJobs;
using UserManagementService.Infrastructure.Hashing;
using UserManagementService.Infrastructure.Persistence;
using UserManagementService.Infrastructure.Persistence.Repositories;
using UserManagementService.Infrastructure.Services;

namespace UserManagementService.Infrastructure;

public static class DependencyInjection
{
    public static object MassTransitLicense { get; private set; }

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // 1. Database Configuration
        var connectionString = configuration.GetConnectionString("Database");
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString));

        // 2. Hashing
        services.AddSingleton<IHasher, Hasher>();

        // 3. Repositories
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped(typeof(IRepository<>), typeof(GenericRepository<>));

        // 4. JWT Provider Setup (FIXED)
        var jwtSettings = configuration.GetSection("Jwt");
        var secretKey = jwtSettings["SecretKey"]!;
        var issuer = jwtSettings["Issuer"]!;
        var audience = jwtSettings["Audience"]!;

        // Manually instantiate JwtProvider with config values
        services.AddSingleton<IJwtProvider>(_ =>
            new JwtProvider(secretKey, issuer, audience));

        // 5. Authentication & JWT Bearer Setup
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = issuer,
                ValidAudience = audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey))
            };
        });

        services.AddAuthorization();

        services.AddScoped<IResetTokenRepository, ResetTokenRepository>();
        services.AddSingleton<IResetPasswordTokenGenerator, ResetPasswordTokenGenerator>();

        // Inside AddInfrastructure method:
        services.AddScoped<IEmailService, EmailService>();

        services.AddMassTransit(x =>
        {
            x.AddConsumer<SendResetCodeConsumer>();

            // Configure the Transactional Outbox
            x.AddEntityFrameworkOutbox<ApplicationDbContext>(o =>
            {
                o.UsePostgres();
                o.UseBusOutbox(); // Automatically move messages to RabbitMQ after DB Save
            });

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host("rabbitmq"); // Match docker-compose name
                cfg.ConfigureEndpoints(context);
            });
        });

        services.AddSingleton<IEmailBodyFactory, EmailBodyFactory>();

        return services;
    }
}