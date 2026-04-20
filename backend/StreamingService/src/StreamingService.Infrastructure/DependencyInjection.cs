using System.Text;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
                cfg.Host(configuration["RabbitMq:Host"] ?? "rabbitmq");
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
        services.AddScoped<IStreamChatMessageRepository, StreamChatMessageRepository>();
        services.AddScoped<IStreamQuestionRepository, StreamQuestionRepository>();


        return services;
    }
}