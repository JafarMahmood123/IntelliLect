using IntelliLect.Contracts.Messages;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StreamingService.Application.Abstractions;
using StreamingService.Domain.Entities;
using StreamingService.Domain.Enums;
using StreamingService.Infrastructure.Consumers;
using StreamingService.Infrastructure.Persistence;

namespace StreamingService.UnitTests;

/// <summary>
/// One live stream per session, guaranteed by the database (test-plan L-01, S-13).
///
/// `SessionStartedConsumerTests` already covers the redelivery it can: publish twice, and the
/// second consume finds the stream and returns. That test passes, and it is not the whole story —
/// the guard it exercises is
///
///     var exists = await _streamRepository.ExistsAsync(...);
///     if (exists) return;
///     ...
///     await _streamRepository.AddAsync(stream);
///
/// which is **two calls**. At-least-once delivery means this message arrives twice sooner or
/// later, and two invocations running at once both pass the check before either insert lands.
/// Nothing then stopped the second one: `SessionId` carried no index at all, unique or otherwise —
/// `HasKey("Id")` and nothing else.
///
/// The consumer's own comment states the consequence: *"Two rows for one session would leave every
/// later lookup picking one arbitrarily."* Which is exactly right — students would join one row
/// while the recording state, participant count and stream key attached to the other, permanently,
/// with no error raised anywhere.
///
/// **A check-then-act race is not closed by checking more carefully.** It is closed by making the
/// second write impossible, so the unique index is the fix and this file is about what it buys:
/// the duplicate insert throws, the retry policy re-runs the consumer, `ExistsAsync` now answers
/// true, and the redelivery ends as the clean no-op it was always meant to be.
///
/// How it was found is worth recording. `SessionStartedConsumerTests` failed once, under load,
/// during an unrelated mutation run — and this file's neighbour `FakeStreamRepository` carries a
/// comment about a previous flake here that "accuses the code under test and is not one". This
/// time the accusation was true.
/// </summary>
public sealed class StreamSessionUniquenessTests
{
    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly Guid ClassroomId = Guid.NewGuid();
    private static readonly Guid TeacherId = Guid.NewGuid();

    /// <summary>
    /// A repository that models the DATABASE, not a dictionary.
    ///
    /// Two behaviours the plain fake does not have: `AddAsync` enforces the unique index, and
    /// `ExistsAsync` can be pinned to `false` for the first N calls — which is what an interleaving
    /// looks like without needing threads to produce one. Driven, not raced, so it fails the same
    /// way every time (the house pattern, per `QuizConcurrencyTests`).
    /// </summary>
    private sealed class ConstrainedStreamRepository : IStreamRepository
    {
        private readonly List<LiveStream> _streams = [];
        private readonly object _gate = new();

        /// <summary>Answers "no" this many times regardless of the truth — the lost race.</summary>
        public int PretendEmptyForFirstCalls { get; init; }

        private int _existsCalls;

        public int SaveCalls { get; private set; }

        public int Count(Guid sessionId)
        {
            lock (_gate) return _streams.Count(stream => stream.SessionId == sessionId);
        }

        public Task<bool> ExistsAsync(Guid sessionId, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _existsCalls++;
                if (_existsCalls <= PretendEmptyForFirstCalls)
                {
                    return Task.FromResult(false);
                }
                return Task.FromResult(_streams.Any(stream => stream.SessionId == sessionId));
            }
        }

        public Task AddAsync(LiveStream entity, CancellationToken ct = default)
        {
            lock (_gate)
            {
                // IX_Streams_SessionId, unique. Npgsql surfaces this as a DbUpdateException on
                // save; the distinction does not matter to the consumer, which rethrows either way.
                if (_streams.Any(stream => stream.SessionId == entity.SessionId))
                {
                    throw new DbUpdateException(
                        "23505: duplicate key value violates unique constraint \"IX_Streams_SessionId\"");
                }
                _streams.Add(entity);
            }
            return Task.CompletedTask;
        }

        public Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            lock (_gate) SaveCalls++;
            return Task.FromResult(1);
        }

        public Task<LiveStream?> GetBySessionIdAsync(
            Guid sessionId, bool includeParticipants = false, CancellationToken ct = default)
        {
            lock (_gate)
                return Task.FromResult(_streams.FirstOrDefault(s => s.SessionId == sessionId));
        }

        public Task<LiveStream?> GetByIdAsync(Guid id, CancellationToken ct = default)
        {
            lock (_gate) return Task.FromResult(_streams.FirstOrDefault(s => s.Id == id));
        }

        public Task UpdateAsync(LiveStream entity, CancellationToken ct = default) => Task.CompletedTask;

        // Not reached by this consumer. Left to throw rather than quietly return a default: a
        // test that started depending on one of these should say so loudly.
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<LiveStream?> GetByEgressIdAsync(string egressId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<LiveStream>> GetLiveStreamsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TryClaimEgressSlotAsync(Guid streamId, string egressId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetEgressIdAsync(Guid streamId, string? egressId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<LiveStream>> GetLiveStreamsNeedingRecordingAsync(string a, DateTime b, CancellationToken ct = default) => throw new NotImplementedException();
    }

    // --- the schema that makes the guarantee ------------------------------------------------

    [Fact]
    public void The_model_declares_a_unique_index_on_the_session()
    {
        // Read from EF's model, with no database and no connection — the same trick
        // `MigrationConformanceTests` uses. This is the assertion that would have failed before
        // the fix, and the one that fails if somebody removes the index later.
        using var context = new StreamingDbContext(
            new DbContextOptionsBuilder<StreamingDbContext>()
                .UseNpgsql("Host=migrations-are-not-applied-here;Database=x;Username=x;Password=x")
                .Options);

        var index = context.Model
            .FindEntityType(typeof(LiveStream))!
            .GetIndexes()
            .SingleOrDefault(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "SessionId" }));

        Assert.True(
            index is not null,
            "Streams.SessionId has no index, so nothing prevents two rows for one session — the "
            + "consumer's ExistsAsync/AddAsync pair is check-then-act and cannot prevent it alone.");
        Assert.True(index!.IsUnique, "the index on Streams.SessionId exists but is not UNIQUE");
    }

    [Fact]
    public void The_migration_creates_it_as_unique()
    {
        // The model and the migration are separate artefacts. `HasPendingModelChanges` catches a
        // model change with no migration; it does not catch a migration edited by hand afterwards,
        // and this constraint is the whole guarantee.
        var migrations = Directory.EnumerateFiles(
            Path.Combine(FindServiceRoot(), "src", "StreamingService.Infrastructure", "Migrations"),
            "*_AddUniqueIndexOnStreamSessionId.cs");

        var source = File.ReadAllText(Assert.Single(migrations));

        Assert.Contains("IX_Streams_SessionId", source);
        Assert.Contains("unique: true", source);
        // A Down that does nothing reports success on rollback and changes nothing.
        Assert.Contains("DropIndex", source);
    }

    // --- what it buys at runtime -------------------------------------------------------------

    [Fact]
    public async Task Two_deliveries_that_both_pass_the_check_still_leave_one_row()
    {
        // The interleaving that the existing redelivery test cannot produce: both invocations ask
        // "does it exist?" before either has inserted, so both are told no. Without the index both
        // insert and the session has two streams forever.
        var streams = new ConstrainedStreamRepository { PretendEmptyForFirstCalls = 2 };
        await using var provider = BuildProvider(streams);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new SessionStartedMessage(SessionId, ClassroomId, TeacherId));
        await harness.Bus.Publish(new SessionStartedMessage(SessionId, ClassroomId, TeacherId));
        await WaitForConsumed(harness, 2);

        Assert.Equal(1, streams.Count(SessionId));
    }

    [Fact]
    public async Task The_loser_of_the_race_faults_so_the_broker_will_retry_it()
    {
        // The insert must not be swallowed. The consumer rethrows, MassTransit faults the message,
        // and the retry is what turns the collision into a no-op — so a swallowed exception would
        // leave the room with one stream (correct by luck) and no record that anything happened.
        var streams = new ConstrainedStreamRepository { PretendEmptyForFirstCalls = 2 };
        await using var provider = BuildProvider(streams);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new SessionStartedMessage(SessionId, ClassroomId, TeacherId));
        await harness.Bus.Publish(new SessionStartedMessage(SessionId, ClassroomId, TeacherId));
        await WaitForConsumed(harness, 2);

        Assert.True(await harness.Published.Any<Fault<SessionStartedMessage>>());
    }

    [Fact]
    public async Task The_retry_after_the_collision_is_a_clean_no_op()
    {
        // What the broker's redelivery meets once the first insert has committed: the check now
        // answers truthfully, and the consumer returns without writing. This is the state the
        // whole arrangement converges on.
        var streams = new ConstrainedStreamRepository();
        await using var provider = BuildProvider(streams);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new SessionStartedMessage(SessionId, ClassroomId, TeacherId));
        await WaitForConsumed(harness, 1);
        await harness.Bus.Publish(new SessionStartedMessage(SessionId, ClassroomId, TeacherId));
        await WaitForConsumed(harness, 2);

        Assert.Equal(1, streams.Count(SessionId));
        Assert.Equal(1, streams.SaveCalls);
        Assert.False(await harness.Published.Any<Fault<SessionStartedMessage>>());
    }

    [Fact]
    public async Task A_different_session_is_never_refused_by_the_constraint()
    {
        // The vacuum guard, and the thing a too-broad constraint would break: uniqueness is per
        // SESSION. An index on the wrong column, or a repository that refused any second insert,
        // would satisfy every assertion above and stop the second lecture of the day starting.
        var streams = new ConstrainedStreamRepository();
        await using var provider = BuildProvider(streams);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var other = Guid.NewGuid();
        await harness.Bus.Publish(new SessionStartedMessage(SessionId, ClassroomId, TeacherId));
        await harness.Bus.Publish(new SessionStartedMessage(other, ClassroomId, TeacherId));
        await WaitForConsumed(harness, 2);

        Assert.Equal(1, streams.Count(SessionId));
        Assert.Equal(1, streams.Count(other));
        Assert.False(await harness.Published.Any<Fault<SessionStartedMessage>>());
    }

    // --- helpers -------------------------------------------------------------------------------

    private static ServiceProvider BuildProvider(IStreamRepository streams)
        => new ServiceCollection()
            .AddSingleton(streams)
            .AddLogging()
            .AddMassTransitTestHarness(x => x.AddConsumer<SessionStartedConsumer>())
            .BuildServiceProvider(true);

    /// <summary>
    /// Bounded wait on the HARNESS rather than on the repository, so a test that should fail
    /// cannot pass by looking too early. Generous, because a busy machine is the condition under
    /// which the original flake appeared.
    /// </summary>
    private static async Task WaitForConsumed(ITestHarness harness, int expected)
    {
        for (var attempt = 0; attempt < 600; attempt++)
        {
            if (harness.Consumed.Select<SessionStartedMessage>().Count() >= expected) return;
            await Task.Delay(10);
        }

        Assert.Fail($"expected {expected} consumed messages, saw "
            + harness.Consumed.Select<SessionStartedMessage>().Count());
    }

    private static string FindServiceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, "src", "StreamingService.Infrastructure")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
