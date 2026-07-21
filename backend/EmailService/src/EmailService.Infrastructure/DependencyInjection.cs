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

        services.AddMassTransit(x =>
        {
            x.AddConsumer<SendResetCodeConsumer>(typeof(SendResetCodeConsumerDefinition));
            x.AddConsumer<SendTwoFactorCodeConsumer>(typeof(SendTwoFactorCodeConsumerDefinition));
            x.AddConsumer<UserStatusChangedConsumer>();
            x.AddConsumer<ClassroomTeacherChangedConsumer>();

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

        return services;
    }
}
