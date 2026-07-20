using Microsoft.EntityFrameworkCore;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
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
    public DbSet<SessionSummary> SessionSummaries => Set<SessionSummary>();


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.AddTransactionalOutboxEntities();

        // Optimistic concurrency for classrooms (use-case alternate path 6أ): map Postgres'
        // system column "xmin" as a concurrency token. It is maintained by Postgres on every
        // row update and needs no schema change, so a stale super-admin edit is rejected with
        // a DbUpdateConcurrencyException instead of silently overwriting a concurrent change.
        modelBuilder.Entity<Classroom>()
            .Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        // Deletion lifecycle (this use-case). Stored as int; defaults to Active so existing rows
        // and new classrooms are usable. Indexed because every teacher/student read path filters
        // it out to hide a classroom that is being deleted.
        modelBuilder.Entity<Classroom>(classroom =>
        {
            classroom.Property(c => c.Status)
                .HasConversion<int>()
                .HasDefaultValue(ClassroomStatus.Active);
            classroom.HasIndex(c => c.Status);
        });

        // Sessions carry a denormalized ClassroomId but have NO foreign key to Classroom (the FK was
        // dropped in AddSessionLifecycleTimestamps), so deleting a classroom does not cascade to its
        // sessions — the deletion service removes them explicitly by ClassroomId. Index that column
        // so both the classroom-scoped purge and the impact report are index scans, not seq scans.
        modelBuilder.Entity<Session>()
            .HasIndex(s => s.ClassroomId);

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

        // Session summaries (S-4). Looked up by session on the summary-ready path and listed by
        // classroom, so both are indexed. Mirrors the SessionRecording configuration.
        modelBuilder.Entity<SessionSummary>(summary =>
        {
            summary.HasKey(s => s.Id);
            // One summary per session: unique so a racing insert can't create a duplicate row.
            summary.HasIndex(s => s.SessionId).IsUnique();
            summary.HasIndex(s => s.ClassroomId);
            summary.HasOne(s => s.Session)
                .WithMany()
                .HasForeignKey(s => s.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}