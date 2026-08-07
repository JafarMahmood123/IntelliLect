using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UserManagementService.Domain.Entities;
using UserManagementService.Infrastructure.Persistence;
using UserManagementService.Infrastructure.Persistence.Repositories;

namespace UserManagementService.UnitTests.UserAccounts;

/// <summary>
/// The REAL repository and a REAL provider, because the two things being fixed live below the
/// fake (test-plan A-30, A-31).
///
/// Every other test in this suite drives `StubUserRepository`, and a stub has neither a query
/// translator nor a unique index. Both matter here, and a mutation proved it: removing the
/// normalisation from `UserRepository.FindByEmail` **survived the entire suite**, because nothing
/// executed that method. The read-side chokepoint was mirrored in the stub by convention rather
/// than verified anywhere.
///
/// SQLite in memory, following `QuizRepositoryTests` in ClassroomService. It is not Postgres, and
/// for this question it does not need to be: SQLite compares text with `=` case-sensitively, the
/// same property that made `u.Email == email` split one person into two accounts, and it enforces
/// a unique index the same way. What stays integration work is behaviour under real concurrency
/// and isolation (S-13) — not whether the constraint exists and bites.
/// </summary>
public sealed class UserRepositoryEmailTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;

    public UserRepositoryEmailTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new ApplicationDbContext(_options);
        context.Database.EnsureCreated();
    }

    // --- the lookup ---------------------------------------------------------------------------

    [Theory]
    [InlineData("jafar@example.com")]
    [InlineData("JAFAR@EXAMPLE.COM")]
    [InlineData("Jafar@Example.com")]
    [InlineData("  jafar@example.com  ")]
    public async Task An_account_is_found_however_the_address_was_typed(string typed)
    {
        // The query the real repository issues, against a provider that compares text exactly —
        // which is the whole defect. Before the fix only the first of these four found the row,
        // and the other three were "invalid credentials" for an account sitting in the table.
        await SeedAsync("Jafar@Example.com");

        var found = await Repository().FindByEmail(typed, default);

        Assert.NotNull(found);
        Assert.Equal("jafar@example.com", found!.Email);
    }

    [Fact]
    public async Task A_different_address_is_still_not_found()
    {
        // The vacuum guard. A lookup broadened into matching anything would satisfy every case
        // above and hand out somebody else's account.
        await SeedAsync("jafar@example.com");

        Assert.Null(await Repository().FindByEmail("someone@example.com", default));
        Assert.Null(await Repository().FindByEmail("jafar@example.org", default));
    }

    [Fact]
    public async Task The_role_still_comes_back_with_the_account()
    {
        // FindByEmail Includes the Role, and login reads it to build the token. Rewriting the
        // query is an easy way to drop an Include and turn every login into a null reference.
        await SeedAsync("jafar@example.com");

        var found = await Repository().FindByEmail("jafar@example.com", default);

        Assert.NotNull(found!.Role);
    }

    // --- the constraint, actually enforced ----------------------------------------------------

    [Fact]
    public async Task A_second_account_for_the_same_address_is_refused_by_the_database()
    {
        // Not "the model declares an index" — the insert is attempted and the provider rejects it.
        // This is what makes the check-then-act race in RegisterAsync unwinnable rather than
        // merely unlikely: both callers may pass the existence check, and only one row survives.
        await SeedAsync("jafar@example.com");

        await using var context = new ApplicationDbContext(_options);
        context.Users.Add(NewUser("jafar@example.com", context));

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task A_second_account_differing_only_in_case_is_refused_too()
    {
        // The two halves together. A unique index alone would not have caught this — the stored
        // values would differ — which is why normalising and constraining are one fix and not two.
        await SeedAsync("jafar@example.com");

        await using var context = new ApplicationDbContext(_options);
        context.Users.Add(NewUser("JAFAR@Example.COM", context));

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Two_different_people_can_both_register()
    {
        // The constraint must be per ADDRESS. An index on the wrong column, or one accidentally
        // made unique across something everyone shares, would satisfy both refusals above and
        // stop the second person ever signing up.
        await SeedAsync("jafar@example.com");

        await using var context = new ApplicationDbContext(_options);
        context.Users.Add(NewUser("amina@example.com", context));

        await context.SaveChangesAsync();
        Assert.Equal(2, await context.Users.CountAsync());
    }

    // --- helpers ------------------------------------------------------------------------------

    private UserRepository Repository() => new(new ApplicationDbContext(_options));

    private async Task SeedAsync(string email)
    {
        await using var context = new ApplicationDbContext(_options);
        context.Users.Add(NewUser(email, context));
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// A user attached to the single seeded Role, so several can be added without colliding on it.
    /// </summary>
    private static User NewUser(string email, ApplicationDbContext context)
    {
        var role = context.Roles.FirstOrDefault();
        if (role is null)
        {
            role = Role.Create(RoleName.Student);
            context.Roles.Add(role);
        }

        return new User
        {
            Id = Guid.NewGuid(),
            UserName = "jafar",
            Email = email,
            FirstName = "Jafar",
            LastName = "Mahmood",
            PasswordHash = "H:pass",
            RoleId = role.Id,
            Role = role,
            RefreshTokens = [],
        };
    }

    public void Dispose() => _connection.Dispose();
}
