namespace ClassroomService.Application.Common;

/// <summary>
/// Tells "the downstream failed" apart from "the caller walked away".
///
/// The best-effort calls in this service are wrapped in `catch (Exception)`, which is right for
/// what they were written to handle — StreamingService being slow or down should not fail a
/// session or a quiz. A bare catch also swallows the cancellation of the request's OWN token,
/// and those are different events arriving as the same exception type.
///
/// A downstream timeout surfaces as <see cref="TaskCanceledException"/> with the caller's token
/// untouched. A caller who has gone surfaces as <see cref="OperationCanceledException"/> with
/// the token cancelled. Only the token separates them, which is why this takes one.
///
/// Here the cost of swallowing is worse than wasted work. <c>StreamingInternalClient</c> reports
/// failure by returning <c>false</c>, so a caller who closed their browser mid-request is
/// indistinguishable from StreamingService refusing the call — and the session is recorded as
/// having failed to start a stream that was never actually refused.
/// </summary>
public static class DownstreamFailure
{
    /// <summary>
    /// True when <paramref name="exception"/> is the downstream's fault and the caller is still
    /// waiting — so degrading is the right answer. False when the caller cancelled, which must
    /// propagate.
    /// </summary>
    public static bool ShouldDegrade(Exception exception, CancellationToken ct)
        => exception is not OperationCanceledException || !ct.IsCancellationRequested;
}
