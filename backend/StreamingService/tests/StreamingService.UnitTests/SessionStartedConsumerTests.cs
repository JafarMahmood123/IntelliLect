using IntelliLect.Contracts.Messages;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using StreamingService.Application.Abstractions;
using StreamingService.Domain.Entities;
using StreamingService.Domain.Enums;
using StreamingService.Infrastructure.Consumers;

namespace StreamingService.UnitTests;

/// <summary>
/// The consumer that turns "a session started" into a live stream row — test-plan L-01.
///
/// It had no tests at all, which mattered more than the coverage number says: it holds the only
/// guard that stops a redelivered message creating a second stream for the same session. The
/// broker delivers at least once by design, so that redelivery is not a hypothetical — it is
/// what happens when an acknowledgement is lost, and it happens in normal operation.
///
/// Driven through the in-memory harness, so the message really is published and consumed.
/// </summary>
public sealed class SessionStartedConsumerTests
{
    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly Guid ClassroomId = Guid.NewGuid();
    private static readonly Guid TeacherId = Guid.NewGuid();

    private static ServiceProvider BuildProvider(FakeStreamRepository streams)
        => new ServiceCollection()
            .AddSingleton<IStreamRepository>(streams)
            .AddLogging()
            .AddMassTransitTestHarness(x => x.AddConsumer<SessionStartedConsumer>())
            .BuildServiceProvider(true);

    private static SessionStartedMessage Message() => new(SessionId, ClassroomId, TeacherId);

    /// <summary>
    /// Waits (bounded) until the harness has consumed the expected number of messages. Waiting on
    /// the harness rather than on the repository is what stops a test that should fail from
    /// passing by looking too early.
    /// </summary>
    private static async Task WaitForConsumed(ITestHarness harness, int expected)
    {
        // 600 x 10ms, not 200. The old two-second budget was enough on an idle machine and not
        // on one running two test hosts at once, which is how a mutation sweep leaves it.
        for (var attempt = 0; attempt < 600; attempt++)
        {
            if (harness.Consumed.Select<SessionStartedMessage>().Count() >= expected) return;
            await Task.Delay(10);
        }

        Assert.Fail($"expected {expected} consumed messages, saw "
            + harness.Consumed.Select<SessionStartedMessage>().Count());
    }

    /// <summary>Publishes the message the given number of times and waits for each to be consumed.</summary>
    private static async Task<FakeStreamRepository> ConsumeAsync(int times, FakeStreamRepository? seeded = null)
    {
        var streams = seeded ?? new FakeStreamRepository();
        await using var provider = BuildProvider(streams);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        // One at a time, waiting for each. A REDELIVERY is what this helper models — the same
        // message arriving again after the first was handled — and publishing both up front makes
        // the in-memory transport deliver them concurrently instead, which is a different
        // scenario with a different guarantee behind it. That ambiguity is what made this file
        // fail intermittently on a loaded machine: two concurrent invocations both passed the
        // existence check, and the test was right to complain. The concurrent case now has its
        // own file, `StreamSessionUniquenessTests`, where the database constraint is the subject.
        for (var i = 0; i < times; i++)
        {
            await harness.Bus.Publish(Message());
            await WaitForConsumed(harness, i + 1);
        }

        return streams;
    }

    [Fact]
    public async Task A_started_session_becomes_a_live_stream()
    {
        var streams = await ConsumeAsync(times: 1);

        var stream = streams.Find(SessionId);
        Assert.NotNull(stream);
        Assert.Equal(ClassroomId, stream!.ClassroomId);
        Assert.Equal(TeacherId, stream.TeacherId);
        Assert.Equal(StreamStatus.Live, stream.Status);
        Assert.Equal(1, streams.SaveCalls);
    }

    [Fact]
    public async Task The_stream_gets_a_key_nobody_can_guess_from_the_session_id()
    {
        // The key is what a client presents; deriving it from the session id would make it
        // predictable for anyone who knows which class is running.
        var streams = await ConsumeAsync(times: 1);

        var stream = streams.Find(SessionId)!;
        Assert.False(string.IsNullOrWhiteSpace(stream.StreamKey));
        Assert.DoesNotContain(SessionId.ToString("N"), stream.StreamKey);
    }

    [Fact]
    public async Task A_redelivered_message_does_not_create_a_second_stream()
    {
        // L-01. At-least-once delivery means this arrives twice sooner or later — a lost ack, a
        // retry after a timeout. Two rows for one session would leave every later lookup picking
        // one arbitrarily.
        var streams = await ConsumeAsync(times: 2);

        Assert.Equal(1, streams.Count(SessionId));
        // And the second delivery must not have written anything at all.
        Assert.Equal(1, streams.SaveCalls);
    }

    [Fact]
    public async Task An_already_live_session_is_left_exactly_as_it_was()
    {
        // The guard has to be a no-op, not a repair: overwriting would reset StartedAtUtc and the
        // stream key of a session people are already watching.
        var existing = new LiveStream
        {
            Id = Guid.NewGuid(),
            SessionId = SessionId,
            ClassroomId = ClassroomId,
            TeacherId = TeacherId,
            Status = StreamStatus.Live,
            StartedAtUtc = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc),
            StreamKey = "the-original-key",
        };

        var streams = await ConsumeAsync(times: 1, new FakeStreamRepository(existing));

        var stream = streams.Find(SessionId)!;
        Assert.Equal("the-original-key", stream.StreamKey);
        Assert.Equal(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), stream.StartedAtUtc);
        Assert.Equal(0, streams.SaveCalls);
    }

    [Fact]
    public async Task Two_different_sessions_each_get_their_own_stream()
    {
        // Guards against an over-eager idempotency check keyed on something other than the session.
        var streams = new FakeStreamRepository();
        await using var provider = BuildProvider(streams);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var other = Guid.NewGuid();
        await harness.Bus.Publish(Message());
        await harness.Bus.Publish(new SessionStartedMessage(other, ClassroomId, TeacherId));
        await WaitForConsumed(harness, 2);

        Assert.NotNull(streams.Find(SessionId));
        Assert.NotNull(streams.Find(other));
    }

    [Fact]
    public async Task A_repository_failure_faults_the_message_rather_than_swallowing_it()
    {
        // The consumer logs and rethrows. If it swallowed instead, the broker would be told the
        // session was handled, the retry policy would never run, and a live class would have no
        // stream row with nothing anywhere saying so.
        var streams = new FakeStreamRepository { ThrowOnAdd = true };
        await using var provider = BuildProvider(streams);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(Message());

        Assert.True(await harness.Published.Any<Fault<SessionStartedMessage>>());
    }
}
