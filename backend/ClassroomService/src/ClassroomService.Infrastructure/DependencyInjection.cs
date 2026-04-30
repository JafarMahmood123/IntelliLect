using System.Text;
using Amazon.S3;
using ClassroomService.Application.Abstractions;
using ClassroomService.Application.Services;
using ClassroomService.Infrastructure.Configuration;
using ClassroomService.Infrastructure.Messaging;
using ClassroomService.Infrastructure.Persistence;
using ClassroomService.Infrastructure.Persistence.Repositories;
using ClassroomService.Infrastructure.Services;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace ClassroomService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IClassroomRepository, ClassroomRepository>();
        services.AddScoped<IMembershipRepository, MembershipRepository>();
        services.AddScoped<IClassroomManagementService, ClassroomManagementService>();
        services.AddScoped<IClassroomFileService, ClassroomFileService>();
        services.AddScoped<IMembershipService, MembershipService>();

        var s3Section = configuration.GetSection(S3Settings.SectionName);
        services.Configure<S3Settings>(s3Section);
        var s3Settings = s3Section.Get<S3Settings>();

        services.AddSingleton<IAmazonS3>(sp =>
        {
            var config = new AmazonS3Config
            {
                RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(s3Settings!.Region),
                ForcePathStyle = true
            };

            if (!string.IsNullOrEmpty(s3Settings.ServiceUrl))
            {
                config.ServiceURL = s3Settings.ServiceUrl;
            }

            return new AmazonS3Client("testuser", "testpassword123!", config);
        });

        services.AddScoped<IFileStorageService, S3FileStorageService>();

        // 1. Database Configuration
        var connectionString = configuration.GetConnectionString("Database");
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString));

        // 2. Generic Repository Registration
        // We cast the ApplicationDbContext to DbContext so the GenericRepository can use it
        services.AddScoped<DbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());
        services.AddScoped(typeof(IRepository<>), typeof(GenericRepository<>));

        // 3. JWT Authentication Configuration
        var jwtSettings = configuration.GetSection("Jwt");
        var secretKey = jwtSettings["SecretKey"] ?? "MY_SUPER_DUPER_STRONG_UNEXPECTED_SECRET_KEY";
        var issuer = jwtSettings["Issuer"] ?? "IntelliLect";
        var audience = jwtSettings["Audience"] ?? "IntelliLect";

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

        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<ISessionService, SessionService>();
        services.AddScoped<IEventBus, MassTransitEventBus>();

        services.AddMassTransit(x =>
        {
            x.AddEntityFrameworkOutbox<ApplicationDbContext>(o =>
            {
                o.UsePostgres();
                o.UseBusOutbox();

                o.QueryDelay = TimeSpan.FromSeconds(1);
            });
            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(configuration["RabbitMq:Host"] ?? "rabbitmq");
                cfg.ConfigureEndpoints(context);
            });
        });

        return services;
    }
}