using Microsoft.EntityFrameworkCore;
using StreamingService.Domain.Entities;
using MassTransit;

namespace StreamingService.Infrastructure.Persistence;

public sealed class StreamingDbContext : DbContext
{
    public StreamingDbContext(DbContextOptions<StreamingDbContext> options) : base(options) { }

    public DbSet<LiveStream> Streams => Set<LiveStream>();
    public DbSet<StreamParticipant> Participants => Set<StreamParticipant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.AddTransactionalOutboxEntities();
    }
}