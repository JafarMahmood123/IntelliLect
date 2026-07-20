using MassTransit;

namespace EmailService.Infrastructure.Consumers;

public sealed class SendTwoFactorCodeConsumerDefinition : ConsumerDefinition<SendTwoFactorCodeConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<SendTwoFactorCodeConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(10)));
    }
}
