using System.Text;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using StreamingService.Application.Abstractions;
using StreamingService.Infrastructure.Authentication;
using StreamingService.Infrastructure.Configuration;
using StreamingService.Infrastructure.Consumers;
using StreamingService.Infrastructure.Persistence;
using StreamingService.Infrastructure.Persistence.Repositories;
using StreamingService.Infrastructure.Services;

namespace StreamingService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Database");
        services.AddDbContext<StreamingDbContext>(options =>
            options.UseNpgsql(connectionString));

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

            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    var path = context.HttpContext.Request.Path;

                    if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/stream"))
                    {
                        context.Token = accessToken;
                    }
                    return Task.CompletedTask;
                },
                OnAuthenticationFailed = context =>
                {
                    var logger = context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("JwtBearer");
                    logger.LogWarning(
                        context.Exception,
                        "JWT authentication failed for {Path}: {Message}",
                        context.Request.Path,
                        context.Exception.Message);
                    return Task.CompletedTask;
                },
                OnChallenge = context =>
                {
                    var logger = context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("JwtBearer");
                    logger.LogDebug(
                        "JWT challenge issued for {Path}, Error: {Error}, Description: {Description}",
                        context.Request.Path,
                        context.Error,
                        context.ErrorDescription);
                    return Task.CompletedTask;
                }
            };
        });

        services.AddSingleton<IJwtProvider>(_ =>
            new JwtProvider(secretKey, issuer, audience));

        services.AddAuthorization();

        services.AddMassTransit(x =>
        {
            x.AddConsumer<SessionStartedConsumer>();

            x.AddEntityFrameworkOutbox<StreamingDbContext>(o =>
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

        services.AddScoped<DbContext>(sp => sp.GetRequiredService<StreamingDbContext>());
        services.AddScoped(typeof(IRepository<>), typeof(GenericRepository<>));
        services.AddScoped<IStreamRepository, StreamRepository>();
        services.AddScoped<IParticipantRepository, ParticipantRepository>();
        services.Configure<LiveKitSettings>(configuration.GetSection(LiveKitSettings.SectionName));
        services.AddSingleton<IStreamSettings>(sp =>
            sp.GetRequiredService<IOptions<LiveKitSettings>>().Value);
        services.AddScoped<IMediaProvider, LiveKitMediaProvider>();

        // Session recording via LiveKit Room Composite Egress (R-0). The typed egress options
        // sit alongside the LiveKit registration; the egress client wraps the SDK's
        // EgressServiceClient (reusing the same API key/secret) and is stateless -> singleton.
        services.Configure<EgressOptions>(configuration.GetSection(EgressOptions.SectionName));
        services.AddSingleton<ILiveKitEgressClient, LiveKitEgressClient>();
        services.AddScoped<IRecordingEgressService, LiveKitRecordingEgressService>();

        services.AddScoped<IStreamChatMessageRepository, StreamChatMessageRepository>();
        services.AddScoped<IStreamQuestionRepository, StreamQuestionRepository>();

        // LiveAssistantService internal client (LA-6): typed HttpClient + options.
        // BaseAddress/timeout come from the "LiveAssistant" section; the shared secret
        // is attached per-request by the client. Best-effort — see the caller's wrapper.
        services.Configure<LiveAssistantOptions>(
            configuration.GetSection(LiveAssistantOptions.SectionName));
        services.AddHttpClient<ILiveAssistantInternalClient, LiveAssistantInternalClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<LiveAssistantOptions>>().Value;
            var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl)
                ? "http://live-assistant-service:8080"
                : options.BaseUrl;
            if (!baseUrl.EndsWith('/'))
            {
                baseUrl += "/";
            }
            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 5);
        });

        return services;
    }
}