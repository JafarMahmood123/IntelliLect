using System.Security.Claims;
using System.Text;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;
using UserManagementService.Domain.Entities;
using UserManagementService.Infrastructure.Authentication;
using UserManagementService.Infrastructure.Hashing;
using UserManagementService.Infrastructure.Messaging;
using UserManagementService.Infrastructure.Persistence;
using UserManagementService.Infrastructure.Services;
using UserManagementService.Infrastructure.Persistence.Repositories;

namespace UserManagementService.Infrastructure;

public static class DependencyInjection
{
    public static object MassTransitLicense { get; private set; }

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Database");
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddSingleton<IHasher, Hasher>();

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IAdminRepository, AdminRepository>();
        services.AddScoped(typeof(IRepository<>), typeof(GenericRepository<>));

        var jwtSettings = configuration.GetSection("Jwt");
        var secretKey = jwtSettings["SecretKey"]!;
        var issuer = jwtSettings["Issuer"]!;
        var audience = jwtSettings["Audience"]!;

        services.AddSingleton<IJwtProvider>(_ =>
            new JwtProvider(secretKey, issuer, audience));

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            // Keep JWT claim names as-issued (e.g. "amr", "uid") instead of remapping short
            // names to long WS-* URIs, so the two-factor policy can match "amr" directly.
            options.MapInboundClaims = false;

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = issuer,
                ValidAudience = audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)),
                RoleClaimType = ClaimTypes.Role
            };
        });

        services.AddAuthorization(options =>
        {
            // Admin-management actions require a super admin whose session has completed 2FA.
            options.AddPolicy(AuthorizationPolicies.SuperAdminTwoFactor, policy =>
                policy
                    .RequireRole(RoleName.SuperAdmin.ToString())
                    .RequireClaim(TwoFactorClaims.ClaimType, TwoFactorClaims.CompletedValue));
        });

        services.AddScoped<IResetTokenRepository, ResetTokenRepository>();
        services.AddSingleton<IResetPasswordTokenGenerator, ResetPasswordTokenGenerator>();

        services.AddScoped<ITwoFactorChallengeRepository, TwoFactorChallengeRepository>();
        services.AddSingleton<ITwoFactorCodeGenerator, TwoFactorCodeGenerator>();

        // Typed HttpClient to ClassroomService's internal endpoint (super admin user-detail view).
        var classroomBaseUrl = configuration["ClassroomService:BaseUrl"] ?? "http://classroom-service:8080";
        var classroomTimeoutSeconds = int.TryParse(configuration["ClassroomService:TimeoutSeconds"], out var seconds) ? seconds : 10;
        services.AddHttpClient<IClassroomInternalClient, ClassroomInternalClient>(client =>
        {
            client.BaseAddress = new Uri(classroomBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(classroomTimeoutSeconds);
        });

        // Real-time inputs for the super-admin session monitor. Both are best-effort at the
        // call site: a failure degrades the live view instead of blocking it (4أ).
        var streamingBaseUrl = configuration["StreamingService:BaseUrl"] ?? "http://streaming-service:8080";
        services.AddHttpClient<IStreamingInternalClient, StreamingInternalClient>(client =>
        {
            client.BaseAddress = new Uri(streamingBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        var liveAssistantBaseUrl = configuration["LiveAssistant:BaseUrl"] ?? "http://live-assistant-service:8080";
        services.AddHttpClient<ILiveAssistantInternalClient, LiveAssistantInternalClient>(client =>
        {
            client.BaseAddress = new Uri(liveAssistantBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        // KnowledgeService admin client (super-admin content/knowledge-base management).
        var knowledgeBaseUrl = configuration["KnowledgeService:BaseUrl"] ?? "http://knowledge-service:8080";
        var knowledgeTimeoutSeconds = int.TryParse(configuration["KnowledgeService:TimeoutSeconds"], out var kSeconds) ? kSeconds : 10;
        services.AddHttpClient<IKnowledgeAdminClient, KnowledgeAdminClient>(client =>
        {
            client.BaseAddress = new Uri(knowledgeBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(knowledgeTimeoutSeconds);
        });

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
                    cfg.Host(configuration["RabbitMq:Host"] ?? "rabbitmq", h =>
                    {
                        h.Username("jafar.mahmood");
                        h.Password("Jafar123!");
                    });
                    cfg.ConfigureEndpoints(context);
                });
            });

        services.AddScoped<IEventBus, MassTransitEventBus>();
        services.AddScoped<INotificationBus, DirectNotificationBus>();
        services.AddScoped<IRoleRepository, RoleRepository>();


        return services;
    }
}
