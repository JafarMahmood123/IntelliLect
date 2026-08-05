using MassTransit;

namespace EmailService.Infrastructure.Consumers;

/// <summary>
/// Retry policy for the three notification consumers.
///
/// They previously had none, while the reset-code and 2FA consumers did. The asymmetry was not a
/// decision anyone made: it meant a single SMTP hiccup sent an approval or enrolment email
/// straight to the error queue, and nobody is watching an error queue for the email that told a
/// student they were accepted. SMTP failures are overwhelmingly transient — a dropped connection,
/// a momentary throttle — so the same three-attempt policy applies here.
///
/// One policy expressed once, in three definitions, because MassTransit binds a definition to a
/// consumer type. The interval matches the code consumers deliberately: a retry cadence that
/// varies per message type is one more thing to reason about when mail stops arriving.
/// </summary>
internal static class NotificationRetry
{
    internal static void Apply(IReceiveEndpointConfigurator endpointConfigurator)
        => endpointConfigurator.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(10)));
}

public sealed class UserStatusChangedConsumerDefinition : ConsumerDefinition<UserStatusChangedConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<UserStatusChangedConsumer> consumerConfigurator,
        IRegistrationContext context)
        => NotificationRetry.Apply(endpointConfigurator);
}

public sealed class ClassroomTeacherChangedConsumerDefinition
    : ConsumerDefinition<ClassroomTeacherChangedConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ClassroomTeacherChangedConsumer> consumerConfigurator,
        IRegistrationContext context)
        => NotificationRetry.Apply(endpointConfigurator);
}

public sealed class ClassroomMembershipChangedConsumerDefinition
    : ConsumerDefinition<ClassroomMembershipChangedConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ClassroomMembershipChangedConsumer> consumerConfigurator,
        IRegistrationContext context)
        => NotificationRetry.Apply(endpointConfigurator);
}
