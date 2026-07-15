using Microsoft.EntityFrameworkCore;
using ClassroomService.Domain.Entities;
using MassTransit;

namespace ClassroomService.Infrastructure.Persistence;

public sealed class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<Classroom> Classrooms => Set<Classroom>();
    public DbSet<ClassroomFile> ClassroomFiles => Set<ClassroomFile>();
    public DbSet<ClassroomMembership> ClassroomMemberships => Set<ClassroomMembership>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<SessionRecording> SessionRecordings => Set<SessionRecording>();


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.AddTransactionalOutboxEntities();

        // Session recordings (R-1). Looked up by session on the recording-ready path and listed
        // by classroom (R-2), so both are indexed. EgressId is the LiveKit correlation id.
        modelBuilder.Entity<SessionRecording>(recording =>
        {
            recording.HasKey(r => r.Id);
            recording.Property(r => r.EgressId).IsRequired();
            // One recording per session: unique so a racing insert can't create a duplicate row.
            recording.HasIndex(r => r.SessionId).IsUnique();
            recording.HasIndex(r => r.ClassroomId);
            recording.HasOne(r => r.Session)
                .WithMany()
                .HasForeignKey(r => r.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}