using MassTransit;

namespace ClassroomService.Infrastructure.Messaging;

/// <summary>
/// Retry policy for <see cref="SessionRecordingReadyConsumer"/>.
/// </summary>
/// <remarks>
/// <para>
/// This was missing, and it was missing next to the one that has it. The summary consumer was
/// given a definition and the recording consumer — registered on the line directly above it —
/// was not, so a single database blip while writing the outcome sent the message to the error
/// queue after one attempt.
/// </para>
/// <para>
/// What that costs is the whole point of the feature. By the time this message arrives the
/// recording already exists in MinIO: the class was captured, the egress finished, the object is
/// sitting there. This consumer is the only thing that turns it into a row the classroom can
/// show. Lose the message and the lecture is recorded and invisible — and nobody finds out,
/// because the failure is a message in a queue nobody watches rather than an error anyone sees.
/// There is no second publish to fall back on.
/// </para>
/// <para>
/// Matches the cadence used by <see cref="SessionSummaryReadyConsumerDefinition"/> and
/// EmailService (3 × 10s) rather than inventing a third one. Deliberately does NOT set an
/// endpoint name formatter: that would rename the live <c>session-recording-ready</c> queue and
/// orphan anything already in it.
/// </para>
/// </remarks>
public sealed class SessionRecordingReadyConsumerDefinition
    : ConsumerDefinition<SessionRecordingReadyConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<SessionRecordingReadyConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(10)));
    }
}
