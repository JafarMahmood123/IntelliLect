namespace UserManagementService.Application.Common;

/// <summary>
/// Tells "the downstream failed" apart from "the caller walked away".
///
/// Every best-effort call in this service is wrapped in `catch (Exception)`, which is correct
/// for what it was written to handle — a downstream that is slow, refusing, or down should
/// degrade one panel of an admin page rather than fail the whole request. But a bare catch
/// also swallows the cancellation of the request's OWN token, and those are not the same
/// event even though they arrive as the same exception type.
///
/// A downstream timeout surfaces as <see cref="TaskCanceledException"/> with the caller's
/// token untouched. A caller who has gone — closed tab, gateway timeout, shutdown — surfaces
/// as <see cref="OperationCanceledException"/> with the token cancelled. Only the token can
/// separate them, which is why this takes one.
///
/// Swallowing the second costs three things. The request keeps working after nobody is left to
/// receive the answer, and keeps calling downstream services to build it. The degradation flag
/// then reports those services as unavailable when nothing was wrong with them, so the signal
/// an operator would use to find a real outage fires on ordinary browser navigation. And the
/// abandoned request returns 200 instead of reaching `GlobalExceptionHandler`, which exists to
/// record it as 499 — the two changes cancel out, and the visibility bought there is lost here.
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
