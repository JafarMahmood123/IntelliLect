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
        // Required, never defaulted. This used to fall back to a literal secret written in this
        // file — which meant a missing Jwt__SecretKey did not break anything, it just made the
        // service validate tokens signed with a key that is public in the git history. Anyone
        // could then mint a token for any role and it would verify. A signing key with a default
        // is not a signing key.
        var secretKey = Required(configuration, "Jwt:SecretKey");
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

        services.AddAuthorization();

        // Read EAGERLY, before MassTransit is configured. The Required() calls used to sit inside
        // the UsingRabbitMq callback, which MassTransit defers until the bus starts — so a missing
        // credential surfaced after the container was built, wrapped in bus-startup noise, instead
        // of at the point the comment below claims. Reading them here makes "fails at startup
        // naming the key" literally true, and testable.
        var brokerUser = Required(configuration, "RabbitMq:Username");
        var brokerPassword = Required(configuration, "RabbitMq:Password");

        services.AddMassTransit(x =>
        {
            // With its definition: without one this consumer got a single attempt, and losing it
            // means a class that has already started has no stream row and cannot recover.
            x.AddConsumer<SessionStartedConsumer>(typeof(SessionStartedConsumerDefinition));

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
                        // Read, never hardcoded: a broker credential in source is a credential in
                        // the git history. Required rather than defaulted, so a missing value fails
                        // at startup with the key name instead of obscurely on the first publish.
                        h.Username(brokerUser);
                        h.Password(brokerPassword);
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

        // Client media quality/reconnection settings, handed to the browser in the join response
        // (see IMediaSettings for why the server owns these rather than a frontend .env).
        // Same port/adapter shape as LiveKitSettings above; immutable options -> singleton.
        services.Configure<MediaOptions>(configuration.GetSection(MediaOptions.SectionName));
        services.AddSingleton<IMediaSettings>(sp =>
            sp.GetRequiredService<IOptions<MediaOptions>>().Value);

        // Server-side room control: closing a room on session end disconnects every remaining
        // participant. Stateless wrapper over the SDK's RoomServiceClient -> singleton.
        services.AddSingleton<ILiveKitRoomClient, LiveKitRoomClient>();
        services.AddSingleton<IRoomLifecycleService, LiveKitRoomLifecycleService>();

        // Session recording via LiveKit Room Composite Egress (R-0). The typed egress options
        // sit alongside the LiveKit registration; the egress client wraps the SDK's
        // EgressServiceClient (reusing the same API key/secret) and is stateless -> singleton.
        services.Configure<EgressOptions>(configuration.GetSection(EgressOptions.SectionName));
        services.AddSingleton<ILiveKitEgressClient, LiveKitEgressClient>();
        services.AddScoped<IRecordingEgressService, LiveKitRecordingEgressService>();

        // The single place a recording is started. Shared by the room_started webhook, the reconcile
        // loop and the teacher's toggle so all three arbitrate through the same database claim —
        // scoped because it writes through the repository.
        services.AddScoped<IRecordingStarter, RecordingStarter>();

        // Reconciliation safety net: recording is otherwise driven by a single unretried
        // room_started webhook, so a missed delivery loses a whole lecture's recording silently.
        // Registered only when recording is on, and skippable by setting the interval to 0.
        var egressOptions = configuration.GetSection(EgressOptions.SectionName).Get<EgressOptions>();
        if (egressOptions is null or { Enabled: true, ReconcileIntervalSeconds: > 0 })
        {
            services.AddScoped<EgressReconciler>();
            services.AddHostedService<EgressReconcileHostedService>();
        }

        // Egress-complete webhook -> recording-ready event (R-1). The verifier wraps the SDK's
        // WebhookReceiver (reusing the LiveKit API key/secret); the handler correlates + publishes.
        services.AddSingleton<ILiveKitWebhookVerifier, LiveKitWebhookVerifier>();
        services.AddScoped<IRecordingWebhookHandler, LiveKitRecordingWebhookHandler>();

        // Capture metrics (R-5): Meter-based, singleton so the underlying Meter is shared.
        services.AddSingleton<IRecordingMetrics, Observability.RecordingMetrics>();

        // SignalR fan-out latency (§9.2): same shape, and a singleton for the same reason — a
        // per-request Meter would register a fresh instrument on every broadcast and the
        // histogram would never accumulate.
        services.AddSingleton<IBroadcastMetrics, Observability.BroadcastMetrics>();

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
