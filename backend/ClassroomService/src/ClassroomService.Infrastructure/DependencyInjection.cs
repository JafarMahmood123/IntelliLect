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

        // Super-admin knowledge-base file administration (list/by-ids/delete).
        services.AddScoped<IFileAdminRepository, FileAdminRepository>();
        services.AddScoped<IFileAdminService, FileAdminService>();

        // Super-admin recordings/summaries management (combined listing + delete).
        services.AddScoped<IOutputAdminRepository, OutputAdminRepository>();
        services.AddScoped<IOutputAdminService, OutputAdminService>();
        services.AddScoped<IClassroomManagementService, ClassroomManagementService>();
        services.AddScoped<IClassroomFileService, ClassroomFileService>();
        services.AddScoped<IMembershipService, MembershipService>();

        // Super-admin classroom member management (list/add/remove students).
        services.AddScoped<IClassroomMemberAdminService, ClassroomMemberAdminService>();
        services.AddScoped<IClassroomRecordingService, ClassroomRecordingService>();
        services.AddScoped<IRecordingRepository, RecordingRepository>();

        // The one path that brings a live session to Ended — shared by the teacher's end button,
        // the super-admin force-end and the stalled-session sweep.
        services.AddScoped<ISessionTerminationService, SessionTerminationService>();

        // Super-admin session monitoring + force-end orchestration.
        services.AddScoped<ISessionAdminService, SessionAdminService>();

        // Super-admin session deletion (impact preview + phased purge of recording/summary/transcript).
        services.AddScoped<ISessionDeletionRepository, SessionDeletionRepository>();
        services.AddScoped<ISessionDeletionService, SessionDeletionService>();

        // Session summaries (S-4): read-side service + repository. Reuses the recording S3 signer
        // (registered below); only the download-URL TTL is a new "Summaries" option.
        services.AddScoped<IClassroomSummaryService, ClassroomSummaryService>();
        services.AddScoped<ISummaryRepository, SummaryRepository>();

        // User-facing classroom Q&A (F-5): membership-scoped wrapper over RagService answering.
        services.AddScoped<IClassroomQaService, ClassroomQaService>();

        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // StreamingService internal client. Same shape as the two below — the base address belongs
        // to configuration, not to the call site.
        services.Configure<StreamingServiceOptions>(
            configuration.GetSection(StreamingServiceOptions.SectionName));
        services.AddHttpClient<IStreamingInternalClient, StreamingInternalClient>(ConfigureStreamingClient);

        // RagService internal client: typed HttpClient + options (mirrors the
        // streaming client). BaseAddress/timeout come from the "RagService" section;
        // the shared secret is attached per-request by the client itself.
        services.Configure<RagServiceOptions>(
            configuration.GetSection(RagServiceOptions.SectionName));
        services.AddHttpClient<IRagInternalClient, RagInternalClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<RagServiceOptions>>().Value;
            var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl)
                ? "http://rag-service:8080"
                : options.BaseUrl;
            if (!baseUrl.EndsWith('/'))
            {
                baseUrl += "/";
            }
            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 10);
        });

        // LiveAssistantService internal client: owns session transcripts, called when a session or
        // classroom is deleted. Same shape as the RagService client.
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
            // The client-wide timeout must cover the LONGEST operation (quiz generation), because
            // HttpClient.Timeout can only be shortened per request, never extended. The quick
            // transcript calls impose their own shorter budget via a linked token, so raising this
            // does not let them hang.
            var generationTimeout = options.GenerationTimeoutSeconds > 0
                ? options.GenerationTimeoutSeconds
                : 120;
            var requestTimeout = options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 10;
            client.Timeout = TimeSpan.FromSeconds(Math.Max(generationTimeout, requestTimeout));
        });

        var s3Section = configuration.GetSection(S3Settings.SectionName);
        services.Configure<S3Settings>(s3Section);
        var s3Settings = s3Section.Get<S3Settings>();

        S3ClientFactory.EnsureCredentialsConfigured(s3Settings);

        // Main client — talks to MinIO over the internal endpoint for uploads/deletes/byte reads.
        services.AddSingleton<IAmazonS3>(_ => S3ClientFactory.Create(s3Settings!, s3Settings!.ServiceUrl));

        services.AddScoped<IFileStorageService, S3FileStorageService>();

        // Pre-sign client — signs GET download URLs against the BROWSER-reachable endpoint so the
        // links resolve from the user's browser (SigV4 signs the host, so it must match what the
        // browser hits). Presigning is local; this client never connects. Falls back to ServiceUrl.
        var presignServiceUrl = string.IsNullOrWhiteSpace(s3Settings!.PublicServiceUrl)
            ? s3Settings.ServiceUrl
            : s3Settings.PublicServiceUrl;
        var presignS3Client = S3ClientFactory.Create(s3Settings, presignServiceUrl);

        // Recording downloads (R-3): reuse the S3 client/bucket above; only the URL TTL is new.
        var recordingsSection = configuration.GetSection(RecordingsOptions.SectionName);
        services.Configure<RecordingsOptions>(recordingsSection);
        services.AddSingleton<IRecordingDownloadSettings>(sp =>
            sp.GetRequiredService<IOptions<RecordingsOptions>>().Value);
        services.AddScoped<IRecordingUrlSigner>(sp =>
            new S3RecordingUrlSigner(presignS3Client, sp.GetRequiredService<IOptions<S3Settings>>()));

        // In-session quizzes. The limits are a singleton settings object like the two above, and
        // are ALSO served to the browser (see QuizzesController) so the composer's bounds and the
        // publish validation can never disagree.
        services.Configure<QuizOptions>(configuration.GetSection(QuizOptions.SectionName));
        services.AddSingleton<IQuizSettings>(sp =>
            sp.GetRequiredService<IOptions<QuizOptions>>().Value);

        // Material upload limits. Same singleton-settings shape, and also served to the browser
        // (see ClassroomFilesController) so the upload control and the server agree on one value.
        services.Configure<UploadOptions>(configuration.GetSection(UploadOptions.SectionName));
        services.AddSingleton<IUploadSettings>(sp =>
            sp.GetRequiredService<IOptions<UploadOptions>>().Value);
        services.AddScoped<IQuizRepository, QuizRepository>();
        services.AddScoped<IQuizService, QuizService>();
        // Pushes state changes to the live room via StreamingService; best-effort, id-only payload.
        // Shares the streaming base address configured above.
        services.AddHttpClient<IQuizNotifier, StreamingQuizNotifier>(ConfigureStreamingClient);
        // Closes quizzes whose time has run out. Registered unconditionally, unlike the two sweeps
        // below: those are safety nets for states that should not arise, whereas a quiz reaching
        // its deadline is the NORMAL end of one, and Closed is what releases the class's marks.
        services.AddScoped<IQuizDeadlineSweeper, QuizDeadlineSweeper>();
        services.AddHostedService<QuizDeadlineHostedService>();

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

        // Stalled-session safety net: closes sessions a teacher never ended. The background job is
        // only registered when the sweep is enabled; the sweeper itself stays registered either
        // way so it can be invoked/tested without the timer.
        var sessionsSection = configuration.GetSection(SessionsOptions.SectionName);
        services.Configure<SessionsOptions>(sessionsSection);
        services.AddSingleton<IStalledSessionSettings>(sp =>
            sp.GetRequiredService<IOptions<SessionsOptions>>().Value);
        services.AddScoped<IStalledSessionSweeper, StalledSessionSweeper>();

        var sessionsOptions = sessionsSection.Get<SessionsOptions>() ?? new SessionsOptions();
        if (sessionsOptions.StalledSweepEnabled)
        {
            services.AddHostedService<StalledSessionSweepHostedService>();
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
        });

        services.AddAuthorization();

        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<ISessionService, SessionService>();
        services.AddScoped<IEventBus, MassTransitEventBus>();

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

            x.AddConsumer<SessionRecordingReadyConsumer>();
            // With the definition, so a transient DB failure while recording the outcome retries
            // instead of dead-lettering and leaving the classroom stuck on Generating.
            x.AddConsumer<SessionSummaryReadyConsumer>(typeof(SessionSummaryReadyConsumerDefinition));
        });

        return services;
    }

    /// <summary>
    /// Base address and timeout for the two clients that call StreamingService. Shared so the
    /// internal client and the quiz notifier can never end up pointing at different hosts.
    /// </summary>
    private static void ConfigureStreamingClient(IServiceProvider sp, HttpClient client)
    {
        var options = sp.GetRequiredService<IOptions<StreamingServiceOptions>>().Value;
        var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl)
            ? "http://streaming-service:8080"
            : options.BaseUrl;
        if (!baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }
        client.BaseAddress = new Uri(baseUrl);
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 10);

        // Attached here rather than in each client, for the same reason the base address is: two
        // clients call this service, and one of them remembering the header is not a state worth
        // having. StreamingService's guard fails closed, so a blank secret fails loudly at the
        // first call instead of quietly working until someone turns the guard on.
        if (!string.IsNullOrWhiteSpace(options.InternalApiSecret))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-Internal-Secret", options.InternalApiSecret);
        }
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
