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

        // One account per address, enforced by the DATABASE rather than by a check in the service.
        //
        // `RegisterAsync` asks "is this taken?" with FindByEmail and then inserts — two calls, and
        // nothing between them. Two requests arriving together both find nothing and both insert,
        // and this needs no unusual timing to reach: a double-clicked Register button is two
        // requests. `SuperAdminService` creates administrators the same way.
        //
        // Two rows for one address is worse here than almost anywhere else in the system, because
        // every lookup is FirstOrDefault: the owner signs in to whichever row the query happens to
        // return, an administrator approves the other, and a password reset updates a third
        // arrangement of the same two. Nothing reports an error at any point.
        //
        // The comparison is exact, which is why `User.Email` normalises on write and
        // `FindByEmail` normalises what it is given — the index can only enforce identity on the
        // value actually stored. See `EmailIdentity`.
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();

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