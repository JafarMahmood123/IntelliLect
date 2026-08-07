using System.Net;
using ClassroomService.Application.Common;
using ClassroomService.Domain.Enums;
using ClassroomService.Infrastructure.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace ClassroomService.UnitTests;

/// <summary>
/// A downstream that fails is reported as a failure; a caller who has gone is not (test-plan L-06).
///
/// This client reports trouble by returning `false`, which makes swallowing the wrong exception
/// more expensive here than anywhere else in the platform. `catch (Exception)` turned a caller's
/// cancellation into `false` — indistinguishable from StreamingService refusing the call — so a
/// teacher whose browser gave up mid-request left a session recorded as having failed to start a
/// stream that was never actually refused. The wrong ANSWER, not merely wasted work.
///
/// The distinction cannot be made on exception type: an HttpClient timeout and an abandoned
/// caller both arrive as `TaskCanceledException`. Only the token separates them.
/// </summary>
public sealed class StreamingInternalClientTests
{
    private static StreamingInternalClient Client(HttpMessageHandler handler)
        => new(
            new HttpClient(handler) { BaseAddress = new Uri("http://streaming-service:8080") },
            NullLogger<StreamingInternalClient>.Instance);

    private static StreamingQuizNotifier Notifier(HttpMessageHandler handler)
        => new(
            new HttpClient(handler) { BaseAddress = new Uri("http://streaming-service:8080") },
            NullLogger<StreamingQuizNotifier>.Instance);

    [Fact]
    public async Task A_timeout_reaching_streaming_is_reported_as_a_failed_call()
    {
        // The case L-06 names: the downstream did not answer in time. `false` is right — the
        // stream really was not created — and the session flow degrades from there.
        var client = Client(new ThrowingHandler(() => new TaskCanceledException("timed out")));

        var created = await client.CreateStreamAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            StudentParticipationMode.AudioAndVideo, recordingEnabled: false, CancellationToken.None);

        Assert.False(created);
    }

    [Fact]
    public async Task A_caller_who_has_gone_is_not_reported_as_streaming_refusing()
    {
        // The defect. Same exception type as above, and `false` here is a lie about a service
        // that was never asked to do anything wrong.
        var client = Client(new ThrowingHandler(() => new OperationCanceledException()));
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.CreateStreamAsync(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                StudentParticipationMode.AudioAndVideo, recordingEnabled: false, source.Token));
    }

    [Fact]
    public async Task Ending_a_stream_makes_the_same_distinction()
    {
        var client = Client(new ThrowingHandler(() => new HttpRequestException("down")));
        Assert.False(await client.EndStreamAsync(Guid.NewGuid(), CancellationToken.None));

        var cancelled = Client(new ThrowingHandler(() => new OperationCanceledException()));
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelled.EndStreamAsync(Guid.NewGuid(), source.Token));
    }

    [Fact]
    public async Task A_quiz_broadcast_still_swallows_a_downstream_failure()
    {
        // Deliberately best-effort, and it must stay that way: the quiz is already committed and
        // every client re-reads state on its next request, so a missed push costs a delayed UI
        // update and never correctness. Guarding the cancellation case must not turn this into
        // an endpoint that fails because a notification did.
        var notifier = Notifier(new ThrowingHandler(() => new HttpRequestException("down")));

        await notifier.QuizChangedAsync(Guid.NewGuid(), Guid.NewGuid(), "Published", CancellationToken.None);
    }

    [Fact]
    public async Task A_quiz_broadcast_does_not_swallow_the_caller_leaving()
    {
        var notifier = Notifier(new ThrowingHandler(() => new OperationCanceledException()));
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => notifier.QuizChangedAsync(Guid.NewGuid(), Guid.NewGuid(), "Published", source.Token));
    }

    [Fact]
    public async Task A_healthy_call_is_untouched_by_any_of_this()
    {
        // The guard sits in a `when` filter, so a successful call never reaches it — but a rule
        // that only ever appears in failure tests is one nobody notices breaking the happy path.
        var handler = new CapturingHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Client(handler);

        var created = await client.CreateStreamAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            StudentParticipationMode.AudioAndVideo, recordingEnabled: true, CancellationToken.None);

        Assert.True(created);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void The_rule_itself_is_written_once_and_says_which_side_wins()
    {
        // Both are true when a timeout and an abandonment coincide, and it resolves toward the
        // caller on purpose: building an answer for somebody who is gone is the worse mistake,
        // and the cost of the other choice is one real timeout logged as an abandonment.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.True(DownstreamFailure.ShouldDegrade(new TaskCanceledException(), CancellationToken.None));
        Assert.False(DownstreamFailure.ShouldDegrade(new TaskCanceledException(), cancelled.Token));
        Assert.True(DownstreamFailure.ShouldDegrade(new HttpRequestException(), cancelled.Token));
    }

    /// <summary>An HttpMessageHandler that always fails, with an exception the test chooses.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Func<Exception> _failure;
        public ThrowingHandler(Func<Exception> failure) => _failure = failure;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(_failure());
    }
}
