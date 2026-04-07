using MassTransit;
using UserManagementService.Infrastructure.Persistence;

namespace UserManagementService.Infrastructure.BackgroundJobs;

public class SendResetCodeConsumerDefinition : ConsumerDefinition<SendResetCodeConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<SendResetCodeConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Exponential(
            5,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromSeconds(5)));

        endpointConfigurator.UseEntityFrameworkOutbox<ApplicationDbContext>(context);
    }
}