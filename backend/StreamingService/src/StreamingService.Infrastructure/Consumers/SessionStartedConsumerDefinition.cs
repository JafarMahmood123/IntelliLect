using MassTransit;

namespace StreamingService.Infrastructure.Consumers;

/// <summary>
/// Retry policy for <see cref="SessionStartedConsumer"/>.
/// </summary>
/// <remarks>
/// <para>
/// StreamingService had no consumer definition at all, so its only consumer got one attempt and
/// then the error queue.
/// </para>
/// <para>
/// This consumer creates the <c>LiveStream</c> row for a class that has just started, and it is
/// the only thing that does. A transient database failure here dead-letters the message and the
/// lecture has no stream record — while the teacher and the students are already in the room
/// waiting. The message is never republished, so the class does not recover; it has to be
/// restarted. That is a live failure in front of an audience, produced by a fault that lasted a
/// second.
/// </para>
/// <para>
/// Retrying is safe because the consumer is already idempotent: it checks
/// <c>ExistsAsync(sessionId)</c> and returns without acting when a stream is present, which is
/// what stops a redelivery creating a second stream. Retry and idempotency are the same
/// guarantee from two sides — one is what makes the other affordable.
/// </para>
/// <para>
/// Matches the cadence used by ClassroomService and EmailService (3 × 10s) rather than inventing
/// a third one. Deliberately does NOT set an endpoint name formatter: that would rename the live
/// <c>session-started</c> queue and orphan anything already in it.
/// </para>
/// </remarks>
public sealed class SessionStartedConsumerDefinition
    : ConsumerDefinition<SessionStartedConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<SessionStartedConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(10)));
    }
}
