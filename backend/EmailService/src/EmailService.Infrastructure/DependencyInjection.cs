using EmailService.Application.Abstractions;
using EmailService.Infrastructure.Consumers;
using EmailService.Infrastructure.Services;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EmailService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IEmailBodyFactory, EmailBodyFactory>();
        services.AddScoped<IEmailSender, SmtpEmailSender>();

        // Read EAGERLY, before MassTransit is configured. The Required() calls used to sit inside
        // the UsingRabbitMq callback, which MassTransit defers until the bus starts — so a missing
        // credential surfaced after the container was built, wrapped in bus-startup noise, instead
        // of at the point the comment below claims. Reading them here makes "fails at startup
        // naming the key" literally true, and testable.
        var brokerUser = Required(configuration, "RabbitMq:Username");
        var brokerPassword = Required(configuration, "RabbitMq:Password");

        services.AddMassTransit(x =>
        {
            x.AddConsumer<SendResetCodeConsumer>(typeof(SendResetCodeConsumerDefinition));
            x.AddConsumer<SendTwoFactorCodeConsumer>(typeof(SendTwoFactorCodeConsumerDefinition));
            x.AddConsumer<UserStatusChangedConsumer>(typeof(UserStatusChangedConsumerDefinition));
            x.AddConsumer<ClassroomTeacherChangedConsumer>(typeof(ClassroomTeacherChangedConsumerDefinition));
            x.AddConsumer<ClassroomMembershipChangedConsumer>(typeof(ClassroomMembershipChangedConsumerDefinition));

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(configuration["RabbitMq:Host"] ?? "rabbitmq", h =>
                {
                    // Read, never hardcoded: a broker credential in source is a credential in the
                    // git history. Required rather than defaulted, so a missing value fails at
                    // startup with the key name instead of failing obscurely on the first publish.
                    h.Username(brokerUser);
                    h.Password(brokerPassword);
                });
                cfg.ConfigureEndpoints(context);
            });
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
