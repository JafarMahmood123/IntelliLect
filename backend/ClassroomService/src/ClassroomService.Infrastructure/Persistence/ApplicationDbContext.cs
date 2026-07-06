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
    }
}