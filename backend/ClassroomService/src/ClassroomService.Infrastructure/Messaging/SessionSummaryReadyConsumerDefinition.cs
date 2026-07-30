using MassTransit;

namespace ClassroomService.Infrastructure.Messaging;

/// <summary>
/// Retry policy for <see cref="SessionSummaryReadyConsumer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Covers the last hop only: the message has arrived, so the summary itself already succeeded or
/// permanently failed in KnowledgeService. What can still go wrong here is local — a database
/// blip while writing the status. Without a retry that would dead-letter the message and leave
/// the classroom showing Generating for a summary that is actually finished.
/// </para>
/// <para>
/// Everything upstream of this point is protected differently and deliberately so: KnowledgeService
/// owns generation retries (it has the attempt counter), and its outbox covers the publish. This
/// is not a substitute for either.
/// </para>
/// <para>
/// Matches EmailService's consumer definitions (3 × 10s) rather than inventing a second cadence.
/// Deliberately does NOT set an endpoint name formatter: that would rename the live
/// <c>session-summary-ready</c> queue and orphan anything already in it.
/// </para>
/// </remarks>
public sealed class SessionSummaryReadyConsumerDefinition
    : ConsumerDefinition<SessionSummaryReadyConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<SessionSummaryReadyConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(10)));
    }
}
