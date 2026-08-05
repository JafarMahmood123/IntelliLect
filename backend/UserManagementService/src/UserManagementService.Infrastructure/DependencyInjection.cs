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
using UserManagementService.Infrastructure.Configuration;
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
        // Required, never defaulted. This used to fall back to a literal secret written in this
        // file — which meant a missing Jwt__SecretKey did not break anything, it just made the
        // service validate tokens signed with a key that is public in the git history. Anyone
        // could then mint a token for any role and it would verify. A signing key with a default
        // is not a signing key.
        var secretKey = Required(configuration, "Jwt:SecretKey");
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

        // --- Downstream /api/internal clients -------------------------------------------------
        // Bound and VALIDATED AT STARTUP rather than read by string index at first use (§14.3/4).
        // These four settings blocks used to be inline defaults spread between here and each
        // client, so a missing secret produced a super-admin page of empty panels and a series of
        // 401s that read as the other service being down.
        services.AddInternalServiceOptions<ClassroomServiceOptions>(
            configuration, ClassroomServiceOptions.SectionName);
        services.AddInternalServiceOptions<StreamingServiceOptions>(
            configuration, StreamingServiceOptions.SectionName);
        services.AddInternalServiceOptions<LiveAssistantOptions>(
            configuration, LiveAssistantOptions.SectionName);
        services.AddInternalServiceOptions<RagServiceOptions>(
            configuration, RagServiceOptions.SectionName);

        // Typed HttpClient to ClassroomService's internal endpoint (super admin user-detail view).
        services.AddInternalHttpClient<IClassroomInternalClient, ClassroomInternalClient, ClassroomServiceOptions>();

        // Real-time inputs for the super-admin session monitor. Both are best-effort at the
        // call site: a failure degrades the live view instead of blocking it (4أ), which is why
        // their configured timeouts are the shortest.
        services.AddInternalHttpClient<IStreamingInternalClient, StreamingInternalClient, StreamingServiceOptions>();
        services.AddInternalHttpClient<ILiveAssistantInternalClient, LiveAssistantInternalClient, LiveAssistantOptions>();

        // RagService admin client (super-admin content/knowledge-base management).
        services.AddInternalHttpClient<IRagAdminClient, RagAdminClient, RagServiceOptions>();

        // Read EAGERLY, before MassTransit is configured. The Required() calls used to sit inside
        // the UsingRabbitMq callback, which MassTransit defers until the bus starts — so a missing
        // credential surfaced after the container was built, wrapped in bus-startup noise, instead
        // of at the point the comment below claims. Reading them here makes "fails at startup
        // naming the key" literally true, and testable.
        var brokerUser = Required(configuration, "RabbitMq:Username");
        var brokerPassword = Required(configuration, "RabbitMq:Password");

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
                        // Read, never hardcoded: a broker credential in source is a credential in
                        // the git history. Required rather than defaulted, so a missing value fails
                        // at startup with the key name instead of obscurely on the first publish.
                        h.Username(brokerUser);
                        h.Password(brokerPassword);
                    });
                    cfg.ConfigureEndpoints(context);
                });
            });

        services.AddScoped<IEventBus, MassTransitEventBus>();
        services.AddScoped<INotificationBus, DirectNotificationBus>();
        services.AddScoped<IRoleRepository, RoleRepository>();


        return services;
    }

    /// <summary>
    /// Reads a setting that has no safe default — a broker credential, a shared secret. Throws at
    /// STARTUP naming the missing key, rather than letting the service boot and fail obscurely on
    /// its first use. Treats empty as missing: a blank password is not a configured one.
    /// </summary>
    private static string Required(IConfiguration configuration, string key)
        => !string.IsNullOrWhiteSpace(configuration[key])
            ? configuration[key]!
            : throw new InvalidOperationException(
                $"Required configuration '{key}' is missing. Set it via the environment variable "
                + $"{key.Replace(":", "__")} or in appsettings.");
}
