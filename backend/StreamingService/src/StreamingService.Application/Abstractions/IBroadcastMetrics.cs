namespace StreamingService.Application.Abstractions;

/// <summary>
/// Server-side timing for the SignalR fan-out (work-plan §9.2).
///
/// The end-to-end latency budgets in <c>docs/latency.md</c> are measured from the client, on one
/// clock, because that is the number a user experiences. But when a budget is missed, "it was
/// slow" is not a finding — you need to know whether the time went on the server or on the wire.
/// This is the server half of that split: how long the hub took to hand the message to every
/// connection in the room, measured inside the one process, so no clock skew is involved.
/// </summary>
public interface IBroadcastMetrics
{
    /// <summary>
    /// Records a <em>successful</em> fan-out. Failures are deliberately not recorded: a broadcast
    /// that throws is fast, and feeding fast failures into a latency histogram moves the
    /// percentiles in the reassuring direction, which is the one way a latency metric can lie.
    /// </summary>
    /// <param name="eventName">The client method being invoked, e.g. <c>ReceiveChatMessage</c>.</param>
    void BroadcastCompleted(string eventName, double seconds);
}
