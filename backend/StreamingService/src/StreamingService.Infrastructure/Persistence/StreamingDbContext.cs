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
    }
}