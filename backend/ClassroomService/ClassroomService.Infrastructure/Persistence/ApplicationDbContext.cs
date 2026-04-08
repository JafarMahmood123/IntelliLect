using Microsoft.EntityFrameworkCore;
using ClassroomService.Domain.Entities;

namespace ClassroomService.Infrastructure.Persistence;

public sealed class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<Classroom> Classrooms => Set<Classroom>();
    public DbSet<ClassroomFile> ClassroomFiles => Set<ClassroomFile>();
}