using MassTransit;

namespace EmailService.Infrastructure.Consumers;

public sealed class SendResetCodeConsumerDefinition : ConsumerDefinition<SendResetCodeConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<SendResetCodeConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(10)));
    }
}
