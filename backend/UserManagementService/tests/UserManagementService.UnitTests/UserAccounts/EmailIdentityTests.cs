using Microsoft.EntityFrameworkCore;
using UserManagementService.Domain.Entities;
using UserManagementService.Domain.Policies;
using UserManagementService.Infrastructure.Persistence;

namespace UserManagementService.UnitTests.UserAccounts;

/// <summary>
/// One account per address (test-plan A-27..A-31).
///
/// Found by sweeping for the defect class §7.4b turned up in StreamingService: a check-then-act
/// guard with no database constraint behind it. ClassroomService was clean — recording, summary
/// and three quiz composites all carry unique indexes. **UserManagementService had none at all**,
/// and its check-then-act is in the registration path:
///
///     var existingUser = await _userRepository.FindByEmail(request.Email, ct);
///     if (existingUser != null) throw ...;
///     await _userRepository.AddAsync(user, ct);
///
/// Two defects, one root, and the second needs no concurrency whatsoever.
///
/// **1. The comparison was case-sensitive.** `u.Email == email` is exact in Postgres, so
/// `Jafar@example.com` and `jafar@example.com` were two accounts. No race, no timing — one capital
/// letter. And every consequence follows from every lookup sharing the comparison: the owner signs
/// in only when their capitalisation matches, a reset for the other spelling finds nobody and
/// (correctly, per A-13) answers as though it sent a code, and an administrator approves one row
/// while the person signs in to the other and is told they are pending.
///
/// **2. Nothing stopped two identical registrations.** No unique index on `Users.Email` — the
/// table carried `HasKey("Id")` and an FK index on `RoleId`. A double-clicked Register button is
/// two requests; both find nothing and both insert.
///
/// The two fixes are one fix: normalise so the stored value is canonical, then let the database
/// enforce identity on it. A constraint on a value that has not been canonicalised enforces
/// nothing useful.
/// </summary>
public sealed class EmailIdentityTests
{
    // --- what counts as the same address ------------------------------------------------------

    [Theory]
    [InlineData("Jafar@Example.com", "jafar@example.com")]
    [InlineData("JAFAR@EXAMPLE.COM", "jafar@example.com")]
    [InlineData("  jafar@example.com  ", "jafar@example.com")]
    [InlineData("Jafar.Mahmood+tag@Example.COM", "jafar.mahmood+tag@example.com")]
    public void Addresses_that_differ_only_in_case_or_padding_are_the_same_address(
        string typed, string canonical)
    {
        Assert.Equal(canonical, EmailIdentity.Normalize(typed));
    }

    [Fact]
    public void Different_addresses_stay_different()
    {
        // The vacuum guard. Normalising to a constant would satisfy every case above and collapse
        // the whole user table into one account on the first migration.
        Assert.NotEqual(
            EmailIdentity.Normalize("jafar@example.com"),
            EmailIdentity.Normalize("jafar@example.org"));
        Assert.NotEqual(
            EmailIdentity.Normalize("a.jafar@example.com"),
            EmailIdentity.Normalize("ajafar@example.com"));
    }

    [Fact]
    public void Normalising_is_idempotent()
    {
        // The stored value is normalised and then read back through the same setter, so a
        // normaliser that changed its input twice would drift on every round trip.
        var once = EmailIdentity.Normalize("Jafar@Example.com");
        Assert.Equal(once, EmailIdentity.Normalize(once));
    }

    [Fact]
    public void A_null_or_blank_address_normalises_without_throwing()
    {
        // Reached by a malformed request before validation, and by EF materialising a row whose
        // column is somehow null. Neither should be an exception from a property getter.
        Assert.Equal(string.Empty, EmailIdentity.Normalize(null));
        Assert.Equal(string.Empty, EmailIdentity.Normalize("   "));
    }

    [Fact]
    public void The_lowercasing_does_not_depend_on_the_servers_locale()
    {
        // `ToLower()` under a Turkish locale maps I to ı, so the same address would normalise
        // differently depending on regional settings and an account could become unreachable by
        // moving the container. Asserted by pinning the thread's culture to the one that breaks.
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            Assert.Equal("iii@example.com", EmailIdentity.Normalize("III@example.com"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    // --- the write chokepoint -----------------------------------------------------------------

    [Fact]
    public void The_entity_stores_the_canonical_form_however_it_was_created()
    {
        // On the SETTER, not at the call sites: a User is created by self-registration through
        // AutoMapper, by an administrator creating an administrator, and by the seeder. A rule
        // that must be remembered at each one will be missed at the next one.
        var user = new User { Email = "  Jafar@Example.COM " };

        Assert.Equal("jafar@example.com", user.Email);
    }

    [Fact]
    public void Reassigning_the_address_normalises_too()
    {
        var user = new User { Email = "first@example.com" };

        user.Email = "SECOND@Example.com";

        Assert.Equal("second@example.com", user.Email);
    }

    // --- the constraint behind the check ------------------------------------------------------

    [Fact]
    public void The_model_declares_a_unique_index_on_the_address()
    {
        // Read from EF's model with no database, the way MigrationConformanceTests does. This is
        // the assertion that failed before the fix and the one that fails if the index is dropped.
        using var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql("Host=migrations-are-not-applied-here;Database=x;Username=x;Password=x")
                .Options);

        var index = context.Model
            .FindEntityType(typeof(User))!
            .GetIndexes()
            .SingleOrDefault(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "Email" }));

        Assert.True(
            index is not null,
            "Users.Email has no index, so nothing prevents two accounts for one address — "
            + "RegisterAsync's FindByEmail/AddAsync pair is check-then-act and cannot prevent it.");
        Assert.True(index!.IsUnique, "the index on Users.Email exists but is not UNIQUE");
    }

    [Fact]
    public void The_migration_canonicalises_the_existing_rows_before_constraining_them()
    {
        // Two separate things the migration must do, and EF generates only the second. Without the
        // backfill a row stored as `Jafar@x.com` matches nothing anyone types — every lookup now
        // normalises its input — so the account becomes unreachable BY this migration.
        var migration = Directory
            .EnumerateFiles(MigrationsFolder(), "*_AddUniqueIndexOnUserEmail.cs")
            .Single();
        var source = File.ReadAllText(migration);

        Assert.Contains("lower(btrim(", source);
        Assert.Contains("IX_Users_Email", source);
        Assert.Contains("unique: true", source);
        // The backfill has to run first; constraining before canonicalising can fail on rows that
        // the canonicalisation would have made legal.
        Assert.True(
            source.IndexOf("lower(btrim(", StringComparison.Ordinal)
            < source.IndexOf("CreateIndex", StringComparison.Ordinal),
            "the UPDATE must precede CreateIndex");
        Assert.Contains("DropIndex", source);
    }

    [Fact]
    public void The_seeded_accounts_are_already_canonical()
    {
        // The seeder compares with `u.Email == adminEmail` against its own constants rather than
        // going through FindByEmail. They are lowercase today, so it works; a later
        // `Admin@IntelliLect.com` would seed a second administrator on every start, because its
        // existence check would never match the row it wrote.
        var seeder = File.ReadAllText(Path.Combine(
            ServiceRoot(), "src", "UserManagementService.Infrastructure",
            "Persistence", "Seeder", "DatabaseSeeder.cs"));

        var addresses = System.Text.RegularExpressions.Regex
            .Matches(seeder, @"""([A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,})""")
            .Select(match => match.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(addresses);
        foreach (var address in addresses)
        {
            Assert.Equal(EmailIdentity.Normalize(address), address);
        }
    }

    // --- helpers ------------------------------------------------------------------------------

    private static string MigrationsFolder() => Path.Combine(
        ServiceRoot(), "src", "UserManagementService.Infrastructure", "Persistence", "Migrations");

    private static string ServiceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, "src", "UserManagementService.Infrastructure")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
