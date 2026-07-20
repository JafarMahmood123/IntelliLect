using MassTransit;
using Microsoft.EntityFrameworkCore;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Infrastructure.Persistence;

public sealed class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ResetPasswordToken> ResetPasswordTokens => Set<ResetPasswordToken>();
    public DbSet<TwoFactorChallenge> TwoFactorChallenges => Set<TwoFactorChallenge>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>()
            .Property(u => u.Version)
            .IsConcurrencyToken();

        // Two-factor challenge -> User: required, cascade on user delete, and no
        // navigation collection on the User side (matches the AddTwoFactorAuth migration).
        modelBuilder.Entity<TwoFactorChallenge>()
            .HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade)
            .IsRequired();

        modelBuilder.AddTransactionalOutboxEntities();
    }
}