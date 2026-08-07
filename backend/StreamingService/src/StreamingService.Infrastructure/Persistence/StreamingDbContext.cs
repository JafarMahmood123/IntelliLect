using Microsoft.EntityFrameworkCore;
using StreamingService.Domain.Entities;
using MassTransit;

namespace StreamingService.Infrastructure.Persistence;

public sealed class StreamingDbContext : DbContext
{
    public StreamingDbContext(DbContextOptions<StreamingDbContext> options) : base(options) { }

    public DbSet<LiveStream> Streams => Set<LiveStream>();
    public DbSet<StreamParticipant> Participants => Set<StreamParticipant>();
    public DbSet<StreamChatMessage> ChatMessages => Set<StreamChatMessage>();
    public DbSet<StreamReaction> Reactions => Set<StreamReaction>();
    public DbSet<StreamQuestion> Questions => Set<StreamQuestion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.AddTransactionalOutboxEntities();

        // One stream per session, enforced by the DATABASE rather than by a check in the consumer.
        //
        // `SessionStartedConsumer` guards against a redelivery with `ExistsAsync` then `AddAsync`,
        // and those are two calls. At-least-once delivery means the message arrives twice sooner
        // or later — a lost ack, a broker redelivery while the original is still in flight, two
        // service instances — and two concurrent invocations both pass the existence check before
        // either insert lands. Nothing then stops the second one: `SessionId` carried no index at
        // all, unique or otherwise.
        //
        // The result is two Streams rows for one session, permanently, with no error anywhere. As
        // the consumer's own test comment puts it, "every later lookup picks one arbitrarily" —
        // so some students join one row and the recording state, participant count and stream key
        // attach to the other.
        //
        // A check-then-act race cannot be closed by checking more carefully; it is closed by
        // making the second write impossible. With this index the duplicate insert throws, the
        // retry policy re-runs the consumer, `ExistsAsync` now returns true, and the redelivery
        // ends as the clean no-op it was always meant to be.
        modelBuilder.Entity<LiveStream>()
            .HasIndex(stream => stream.SessionId)
            .IsUnique();
    }
}