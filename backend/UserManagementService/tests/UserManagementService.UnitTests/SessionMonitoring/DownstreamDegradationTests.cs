using UserManagementService.Application.Common;

namespace UserManagementService.UnitTests.SessionMonitoring;

/// <summary>
/// A downstream that fails degrades one panel; a caller who has gone does not (test-plan L-06).
///
/// L-06 sat at `partial` because the degradation paths were tested with `HttpRequestException`
/// and nothing else. That is the easy failure — the connection is refused, the exception is
/// unambiguous, the flag goes up. **The two that matter arrive as the same exception type as
/// each other**, and the existing tests could not tell them apart because they never produced
/// either one:
///
///   * a downstream TIMEOUT is `TaskCanceledException` with the caller's token untouched, and
///     must degrade — that is the case L-06 names;
///   * a CALLER who has gone (closed tab, gateway timeout, shutdown) is
///     `OperationCanceledException` with the token cancelled, and must propagate.
///
/// `catch (Exception)` swallowed both. Nothing failed visibly, which is why it survived: the
/// abandoned request simply kept working, kept calling the other services to finish an answer
/// nobody would read, and returned 200 with the "downstream unavailable" flag raised against
/// services that were fine. That flag is what an operator would look at to find an outage, so
/// ordinary browser navigation was writing false positives into it — and the 499 accounting
/// added to `GlobalExceptionHandler` never saw these requests at all, because the exception was
/// caught two layers below it.
/// </summary>
public sealed class DownstreamDegradationTests
{
    private static readonly CancellationToken Live = CancellationToken.None;

    private static CancellationToken Cancelled()
    {
        var source = new CancellationTokenSource();
        source.Cancel();
        return source.Token;
    }

    [Fact]
    public void A_downstream_timeout_degrades()
    {
        // The case L-06 is written about. HttpClient reports its own timeout as
        // TaskCanceledException — the same type the caller's cancellation produces — and the
        // ONLY thing separating them is whose token was cancelled.
        Assert.True(DownstreamFailure.ShouldDegrade(new TaskCanceledException("timed out"), Live));
    }

    [Fact]
    public void A_caller_who_has_gone_is_not_a_downstream_failure()
    {
        // The defect. Same exception type as the case above, opposite correct answer.
        Assert.False(
            DownstreamFailure.ShouldDegrade(new OperationCanceledException(), Cancelled()));
    }

    [Fact]
    public void A_timeout_while_the_caller_is_also_cancelling_belongs_to_the_caller()
    {
        // Genuinely ambiguous — both are true at once — and it is resolved toward the caller on
        // purpose. Degrading here would build a response for somebody who is not there, and the
        // cost of the other choice is only that one real timeout is reported as an abandonment.
        Assert.False(DownstreamFailure.ShouldDegrade(new TaskCanceledException(), Cancelled()));
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(TimeoutException))]
    public void An_ordinary_downstream_failure_still_degrades(Type failure)
    {
        // The half that must not regress. A guard that propagated everything would turn a
        // single unavailable panel back into a failed page for the whole admin dashboard —
        // which is the outcome the try/catch existed to prevent.
        var exception = (Exception)Activator.CreateInstance(failure)!;

        Assert.True(DownstreamFailure.ShouldDegrade(exception, Live));
    }

    [Fact]
    public void A_failure_that_is_not_a_cancellation_degrades_even_mid_cancellation()
    {
        // A cancelled token does not make every failure the caller's. The downstream really did
        // return an error here, and the pre-existing behaviour for that is still correct.
        Assert.True(DownstreamFailure.ShouldDegrade(new HttpRequestException("down"), Cancelled()));
    }
}
