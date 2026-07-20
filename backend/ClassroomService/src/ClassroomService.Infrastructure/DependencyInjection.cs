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
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ClassroomService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IClassroomRepository, ClassroomRepository>();
        services.AddScoped<IMembershipRepository, MembershipRepository>();

        // Super-admin classroom deletion (impact preview + phased purge).
        services.AddScoped<IClassroomDeletionRepository, ClassroomDeletionRepository>();
        services.AddScoped<IClassroomDeletionService, ClassroomDeletionService>();
        services.AddScoped<IClassroomManagementService, ClassroomManagementService>();
        services.AddScoped<IClassroomFileService, ClassroomFileService>();
        services.AddScoped<IMembershipService, MembershipService>();
        services.AddScoped<IClassroomRecordingService, ClassroomRecordingService>();
        services.AddScoped<IRecordingRepository, RecordingRepository>();

        // Super-admin session monitoring + force-end orchestration.
        services.AddScoped<ISessionAdminService, SessionAdminService>();

        // Session summaries (S-4): read-side service + repository. Reuses the recording S3 signer
        // (registered below); only the download-URL TTL is a new "Summaries" option.
        services.AddScoped<IClassroomSummaryService, ClassroomSummaryService>();
        services.AddScoped<ISummaryRepository, SummaryRepository>();

        // User-facing classroom Q&A (F-5): membership-scoped wrapper over KnowledgeService answering.
        services.AddScoped<IClassroomQaService, ClassroomQaService>();

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddHttpClient<IStreamingInternalClient, StreamingInternalClient>();

        // KnowledgeService internal client: typed HttpClient + options (mirrors the
        // streaming client). BaseAddress/timeout come from the "KnowledgeService" section;
        // the shared secret is attached per-request by the client itself.
        services.Configure<KnowledgeServiceOptions>(
            configuration.GetSection(KnowledgeServiceOptions.SectionName));
        services.AddHttpClient<IKnowledgeInternalClient, KnowledgeInternalClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<KnowledgeServiceOptions>>().Value;
            var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl)
                ? "http://knowledge-service:8080"
                : options.BaseUrl;
            if (!baseUrl.EndsWith('/'))
            {
                baseUrl += "/";
            }
            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 10);
        });

        var s3Section = configuration.GetSection(S3Settings.SectionName);
        services.Configure<S3Settings>(s3Section);
        var s3Settings = s3Section.Get<S3Settings>();

        static AmazonS3Client BuildS3Client(S3Settings s, string? serviceUrl)
        {
            var config = new AmazonS3Config
            {
                RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(s.Region),
                ForcePathStyle = true
            };

            if (!string.IsNullOrEmpty(serviceUrl))
            {
                config.ServiceURL = serviceUrl;
            }

            return new AmazonS3Client("testuser", "testpassword123!", config);
        }

        // Main client — talks to MinIO over the internal endpoint for uploads/deletes/byte reads.
        services.AddSingleton<IAmazonS3>(_ => BuildS3Client(s3Settings!, s3Settings!.ServiceUrl));

        services.AddScoped<IFileStorageService, S3FileStorageService>();

        // Pre-sign client — signs GET download URLs against the BROWSER-reachable endpoint so the
        // links resolve from the user's browser (SigV4 signs the host, so it must match what the
        // browser hits). Presigning is local; this client never connects. Falls back to ServiceUrl.
        var presignServiceUrl = string.IsNullOrWhiteSpace(s3Settings!.PublicServiceUrl)
            ? s3Settings.ServiceUrl
            : s3Settings.PublicServiceUrl;
        var presignS3Client = BuildS3Client(s3Settings, presignServiceUrl);

        // Recording downloads (R-3): reuse the S3 client/bucket above; only the URL TTL is new.
        var recordingsSection = configuration.GetSection(RecordingsOptions.SectionName);
        services.Configure<RecordingsOptions>(recordingsSection);
        services.AddSingleton<IRecordingDownloadSettings>(sp =>
            sp.GetRequiredService<IOptions<RecordingsOptions>>().Value);
        services.AddScoped<IRecordingUrlSigner>(sp =>
            new S3RecordingUrlSigner(presignS3Client, sp.GetRequiredService<IOptions<S3Settings>>()));

        // Summary downloads (S-4): reuse the same S3 signer/bucket; only the URL TTL is new.
        services.Configure<SummariesOptions>(configuration.GetSection(SummariesOptions.SectionName));
        services.AddSingleton<ISummaryDownloadSettings>(sp =>
            sp.GetRequiredService<IOptions<SummariesOptions>>().Value);

        // Recording lifecycle & retention (R-4): delete-over-S3, reconcile & retention logic, and
        // the background job (only registered when a periodic pass is actually enabled).
        services.AddSingleton<IRecordingLifecycleSettings>(sp =>
            sp.GetRequiredService<IOptions<RecordingsOptions>>().Value);
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IRecordingStorage, S3RecordingStorage>();
        services.AddScoped<IRecordingLifecycleService, RecordingLifecycleService>();

        // Recording metrics (R-5): Meter-based, singleton so the underlying Meter is shared.
        services.AddSingleton<IRecordingMetrics, Observability.RecordingMetrics>();

        // Summary metrics (S-5): Meter-based, singleton so the underlying Meter is shared.
        services.AddSingleton<ISummaryMetrics, Observability.SummaryMetrics>();

        var recordingsOptions = recordingsSection.Get<RecordingsOptions>() ?? new RecordingsOptions();
        if (recordingsOptions.ReconcileEnabled || recordingsOptions.RetentionEnabled)
        {
            services.AddHostedService<RecordingReconcileHostedService>();
        }

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
                    cfg.Host(configuration["RabbitMq:Host"] ?? "rabbitmq", h =>
                    {
                        h.Username("jafar.mahmood");
                        h.Password("Jafar123!");
                    });
                    cfg.ConfigureEndpoints(context);
                });

            x.AddConsumer<SessionRecordingReadyConsumer>();
            x.AddConsumer<SessionSummaryReadyConsumer>();
        });

        return services;
    }
}