using ClassroomService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ClassroomService.UnitTests;

/// <summary>
/// Migrations against the model they claim to describe (work-plan §11.8).
///
/// Applying a migration needs a real Postgres, and that stays integration work. What does not is
/// the question that actually goes wrong: **did somebody change an entity and not add a
/// migration?** The model and the last migration's snapshot are both in this assembly, so EF can
/// compare them here with no database at all.
///
/// That failure is quiet and expensive. The code compiles, the tests pass — every one of them
/// uses fakes or SQLite — and the drift only surfaces when a request touches the missing column
/// against the real database. By then the deployment has already happened.
/// </summary>
public sealed class MigrationConformanceTests
{
    /// <summary>
    /// A context wired to Npgsql but never connected. The provider has to be the real one, because
    /// the snapshot was generated for it and comparing against SQLite's would report differences
    /// that are only differences of provider.
    /// </summary>
    private static ApplicationDbContext Context()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=migrations-are-not-applied-here;Database=x;Username=x;Password=x")
            .Options);

    [Fact]
    public void The_model_and_the_migrations_have_not_drifted()
    {
        using var context = Context();

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "An entity or configuration has changed with no migration to match. The next "
            + "deployment will run against a schema that is missing the change — and every test "
            + "here will keep passing, because none of them use the real database. "
            + "Run: dotnet ef migrations add <Name> --project src/ClassroomService.Infrastructure");
    }

    [Fact]
    public void Every_migration_can_be_undone()
    {
        // EF generates a Down for every migration, but it is ordinary code and can be emptied —
        // by hand, or by a generator that could not work out how to reverse something. An empty
        // Down does not fail: `dotnet ef database update <previous>` reports success and changes
        // nothing, which is the worst possible answer during a rollback.
        var empty = MigrationTypes()
            .Select(type => (type.Name, Migration: (Migration)Activator.CreateInstance(type)!))
            .Where(m => m.Migration.UpOperations.Count > 0 && m.Migration.DownOperations.Count == 0)
            .Select(m => m.Name)
            .ToList();

        Assert.True(empty.Count == 0, $"These migrations cannot be rolled back: {string.Join(", ", empty)}");
    }

    [Fact]
    public void Migration_ids_are_unique_and_ordered_by_their_timestamps()
    {
        // EF applies migrations in id order, which is the timestamp prefix. Two migrations sharing
        // one — which happens when a branch is merged, or a file is copied — makes the order
        // undefined, and "undefined" here means one of them may never run.
        using var context = Context();
        var ids = context.Database.GetMigrations().ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(ids.OrderBy(id => id, StringComparer.Ordinal), ids);
    }

    [Fact]
    public void There_are_migrations_and_entities_to_check()
    {
        // Every assertion above passes trivially over an empty set — the most comfortable way for
        // a conformance test to stop meaning anything.
        using var context = Context();

        Assert.True(context.Database.GetMigrations().Any(), "no migrations found");
        Assert.True(context.Model.GetEntityTypes().Count() >= 5, "the model looks empty");
    }

    private static List<Type> MigrationTypes()
        => typeof(ApplicationDbContext).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(Migration).IsAssignableFrom(t))
            .ToList();
}
