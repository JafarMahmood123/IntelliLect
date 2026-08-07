using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StreamingService.Domain.Entities;
using StreamingService.Domain.Enums;
using StreamingService.Infrastructure.Persistence;
using StreamingService.Infrastructure.Persistence.Repositories;

namespace StreamingService.UnitTests;

/// <summary>
/// One row per person per stream, and a participant count that is counted (test-plan L-20..L-24).
///
/// The third instance of the class §7.4b opened, and the easiest of the three to reach. The other
/// two need a broker redelivery or a double-clicked button; this one needs a lecture on a poor
/// connection, which is the situation the reconnection work in P1 exists for.
///
///     var isAlreadyJoined = stream.Participants.Any(p => p.UserId == userId);
///     if (!isAlreadyJoined)
///     {
///         await _participantRepository.AddAsync(new StreamParticipant { ... }, ct);
///
/// Check-then-act, and `Participants` carried `HasKey("Id")` with a plain index on `StreamId`.
/// A LiveKit reconnection re-joins, a second browser tab joins, a retried request joins.
///
/// **What it corrupts is the number on the teacher's screen.** A duplicate inflates the roster and
/// the count; `LeaveStreamAsync` deletes one row, so the person leaves and their ghost remains for
/// the rest of the session; `ToggleHandRaiseAsync` resolves to one of the two arbitrarily, so a
/// raised hand can appear to be ignored.
///
/// **And the count was wrong even without duplicates.** Both broadcasts did arithmetic on a
/// collection loaded before the write — `Participants.Count + 1` on join, `- 1` on leave. Two
/// people joining at once both read the same starting number and both announced it plus one, so
/// the class was told there were fewer people present than there were, and nothing recomputed it
/// until somebody else joined or left. That is a separate defect in the same two methods, and a
/// unique index does not fix it: the count has to be counted.
/// </summary>
public sealed class ParticipantUniquenessTests : IDisposable
{
    private static readonly Guid SessionId = Guid.NewGuid();

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<StreamingDbContext> _options;
    private readonly Guid _streamId = Guid.NewGuid();

    public ParticipantUniquenessTests()
    {
        // A real provider, following QuizRepositoryTests and UserRepositoryEmailTests: a fake has
        // no constraints, and "the constraint exists" is the whole claim being made here.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<StreamingDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new StreamingDbContext(_options);
        context.Database.EnsureCreated();
        context.Streams.Add(new LiveStream
        {
            Id = _streamId,
            SessionId = SessionId,
            ClassroomId = Guid.NewGuid(),
            TeacherId = Guid.NewGuid(),
            Status = StreamStatus.Live,
            StreamKey = "key",
        });
        context.SaveChanges();
    }

    // --- the constraint -----------------------------------------------------------------------

    [Fact]
    public void The_model_declares_a_unique_index_per_stream_and_user()
    {
        using var context = new StreamingDbContext(
            new DbContextOptionsBuilder<StreamingDbContext>()
                .UseNpgsql("Host=migrations-are-not-applied-here;Database=x;Username=x;Password=x")
                .Options);

        var index = context.Model
            .FindEntityType(typeof(StreamParticipant))!
            .GetIndexes()
            .SingleOrDefault(i => i.Properties.Select(p => p.Name)
                .SequenceEqual(new[] { "StreamId", "UserId" }));

        Assert.True(
            index is not null,
            "Participants has no (StreamId, UserId) index, so a reconnect can add a second row for "
            + "the same person — JoinStreamAsync's check-then-add cannot prevent it alone.");
        Assert.True(index!.IsUnique, "the index exists but is not UNIQUE");
    }

    [Fact]
    public void The_migration_creates_it_as_unique_and_restores_what_it_replaced()
    {
        // The model and the migration are separate artefacts, and `HasPendingModelChanges` only
        // compares the model against the SNAPSHOT — never against the migration body. So a
        // migration edited afterwards to say `unique: false` leaves every other test green and
        // ships a schema that permits exactly what this row exists to prevent. Two mutations
        // proved it before this test was written.
        var migration = Directory
            .EnumerateFiles(MigrationsFolder(), "*_AddUniqueIndexOnStreamParticipant.cs")
            .Single();
        var source = File.ReadAllText(migration);

        Assert.Contains("IX_Participants_StreamId_UserId", source);
        Assert.Contains("unique: true", source);

        // The composite replaces the plain StreamId index, so the Down has to put that back —
        // otherwise a rollback silently leaves the table without an index it had before.
        var down = source[source.IndexOf("protected override void Down", StringComparison.Ordinal)..];
        Assert.Contains("DropIndex", down);
        Assert.Contains("IX_Participants_StreamId\"", down);
    }

    [Fact]
    public async Task A_second_row_for_the_same_person_is_refused_by_the_database()
    {
        // Attempted against a real provider, not asserted about the model. This is what makes the
        // reconnect harmless rather than merely unlikely.
        await AddParticipantAsync(_streamId, UserId);

        await using var context = new StreamingDbContext(_options);
        context.Participants.Add(Participant(_streamId, UserId));

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Two_different_people_can_both_be_in_the_stream()
    {
        // The vacuum guard, and what an index on the wrong column would break: uniqueness is per
        // (stream, person). Getting it wrong empties the lecture instead of de-duplicating it.
        await AddParticipantAsync(_streamId, UserId);

        await using var context = new StreamingDbContext(_options);
        context.Participants.Add(Participant(_streamId, Guid.NewGuid()));

        await context.SaveChangesAsync();
        Assert.Equal(2, await context.Participants.CountAsync());
    }

    [Fact]
    public async Task The_same_person_can_be_in_two_different_streams()
    {
        // A teacher moving between their own sessions, or a student in a recorded class and a live
        // one. Constraining on UserId alone would refuse this.
        var other = Guid.NewGuid();
        await using (var setup = new StreamingDbContext(_options))
        {
            setup.Streams.Add(new LiveStream
            {
                Id = other,
                SessionId = Guid.NewGuid(),
                ClassroomId = Guid.NewGuid(),
                TeacherId = Guid.NewGuid(),
                Status = StreamStatus.Live,
                StreamKey = "key-2",
            });
            await setup.SaveChangesAsync();
        }

        await AddParticipantAsync(_streamId, UserId);
        await AddParticipantAsync(other, UserId);

        await using var context = new StreamingDbContext(_options);
        Assert.Equal(2, await context.Participants.CountAsync());
    }

    // --- the count ----------------------------------------------------------------------------

    [Fact]
    public async Task The_count_is_counted_rather_than_derived()
    {
        // The arithmetic it replaces was `Participants.Count + 1` against a collection read before
        // the insert. Here three people are present and the answer must be three however they got
        // there — including after a row was added by someone else since this request started.
        await AddParticipantAsync(_streamId, UserId);
        await AddParticipantAsync(_streamId, Guid.NewGuid());
        await AddParticipantAsync(_streamId, Guid.NewGuid());

        await using var context = new StreamingDbContext(_options);
        var count = await new ParticipantRepository(context).CountInStreamAsync(_streamId, default);

        Assert.Equal(3, count);
    }

    [Fact]
    public async Task The_count_is_scoped_to_one_stream()
    {
        // Counting the table rather than the stream would report every participant in the platform
        // to every room, which is the sort of number nobody questions until it is enormous.
        var other = Guid.NewGuid();
        await using (var setup = new StreamingDbContext(_options))
        {
            setup.Streams.Add(new LiveStream
            {
                Id = other,
                SessionId = Guid.NewGuid(),
                ClassroomId = Guid.NewGuid(),
                TeacherId = Guid.NewGuid(),
                Status = StreamStatus.Live,
                StreamKey = "key-2",
            });
            await setup.SaveChangesAsync();
        }

        await AddParticipantAsync(_streamId, UserId);
        await AddParticipantAsync(other, Guid.NewGuid());
        await AddParticipantAsync(other, Guid.NewGuid());

        await using var context = new StreamingDbContext(_options);
        var repository = new ParticipantRepository(context);

        Assert.Equal(1, await repository.CountInStreamAsync(_streamId, default));
        Assert.Equal(2, await repository.CountInStreamAsync(other, default));
    }

    [Fact]
    public async Task An_empty_stream_counts_zero_rather_than_failing()
    {
        // The leave path's old `Math.Max(0, ...)` existed to stop arithmetic producing a negative
        // number. A real count cannot, and the last person leaving is a normal event.
        await using var context = new StreamingDbContext(_options);

        Assert.Equal(0, await new ParticipantRepository(context).CountInStreamAsync(_streamId, default));
    }

    // --- helpers ------------------------------------------------------------------------------

    private static string MigrationsFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, "src", "StreamingService.Infrastructure")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "StreamingService.Infrastructure", "Migrations");
    }

    private static readonly Guid UserId = Guid.NewGuid();

    private async Task AddParticipantAsync(Guid streamId, Guid userId)
    {
        await using var context = new StreamingDbContext(_options);
        context.Participants.Add(Participant(streamId, userId));
        await context.SaveChangesAsync();
    }

    private static StreamParticipant Participant(Guid streamId, Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        StreamId = streamId,
        UserId = userId,
        JoinedAtUtc = DateTime.UtcNow,
    };

    public void Dispose() => _connection.Dispose();
}
