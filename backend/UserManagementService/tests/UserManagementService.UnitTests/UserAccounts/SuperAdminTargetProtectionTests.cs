using Microsoft.Extensions.Logging;
using UserManagementService.Application.Common.Users;
using UserManagementService.Application.UserAccounts;
using UserManagementService.Domain.Entities;

namespace UserManagementService.UnitTests.UserAccounts;

/// <summary>
/// A super admin's account cannot be changed from the user directory (test-plan C-26..C-30).
///
/// **The super-admin surface could be deactivated out of existence, and there is no way back.**
///
/// `SuperAdminService.DeactivateAdminAsync` refuses a super-admin target already — its repository
/// query is `Where(user => user.Role.Name == RoleName.Admin)`, so passing a super admin's id there
/// is a 404. That filter is deliberate. `UserStatusService` reaches the same accounts through a
/// different door — `PUT /api/super-admin/users/{id}/status` and its bulk sibling — and had no
/// filter at all: it checked the transition, and the actor, and never once looked at who the target
/// was.
///
/// Everything needed is in place for that to be terminal:
///
/// - `Deactivate` requires `Active`, and a seeded super admin is Active.
/// - The self-target guard stops one super admin disabling themselves — but with **two**, A
///   disables B and B disables A, and neither is a self-target.
/// - Deactivation revokes refresh tokens, so it takes effect immediately rather than at expiry.
/// - Nothing creates a super admin. `CreateAdminAsync` mints an **Admin**; the role is only ever
///   assigned by the database seeder, which runs on an empty database.
/// - `SearchUsers` does not exclude them, so their ids are in the directory the button lives on.
///
/// So: no super admin can sign in, no route can appoint one, and the only recovery is editing the
/// database by hand. The bulk path is the easier way to arrive there by accident — "select all,
/// deactivate" over a directory that lists super admins alongside everyone else.
/// </summary>
public sealed class SuperAdminTargetProtectionTests
{
    private static readonly Guid Actor = Guid.NewGuid();

    // --- the single-account route ---------------------------------------------------------------

    [Theory]
    [InlineData("Deactivate")]
    [InlineData("Reactivate")]
    [InlineData("Accept")]
    [InlineData("Reject")]
    public async Task No_action_may_be_applied_to_a_super_admin(string action)
    {
        // Driven over every action rather than the dangerous one, because the rule is "this route
        // does not manage super admins" and not "deactivation is risky". A rule written as the
        // latter leaves `Reject` open, and rejecting an Active account is refused today only by
        // the transition matrix — a matrix somebody could reasonably widen.
        var target = SuperAdmin(UserStatus.Active);
        var repo = new FakeStatusUserRepository(target);
        var log = new AuditLog();
        var sut = Sut(repo, log);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ChangeStatusAsync(target.Id, action, Actor));

        Assert.Equal(UserStatus.Active, target.Status);
        Assert.Equal(0, repo.SaveChangesCallCount);
    }

    [Fact]
    public async Task The_refusal_is_audited_like_every_other_refusal()
    {
        // §7.11b's rule: the log answers "was this account deactivated, by whom, and when". An
        // attempt on a super admin is the single most interesting line it could carry, and a
        // refusal that writes nothing is indistinguishable from an attempt that never happened.
        var target = SuperAdmin(UserStatus.Active);
        var log = new AuditLog();
        var sut = Sut(new FakeStatusUserRepository(target), log);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ChangeStatusAsync(target.Id, "Deactivate", Actor));

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("ProtectedTarget", entry.Message);
        Assert.Contains(Actor.ToString(), entry.Message);
        Assert.Contains(target.Id.ToString(), entry.Message);
    }

    [Fact]
    public async Task Everyone_else_is_still_deactivatable()
    {
        // The vacuum guard, and it is not hypothetical: a rule keyed on the wrong side of the
        // comparison would refuse every account and quietly disable the directory's only button.
        var target = UserWithRole(RoleName.Student, UserStatus.Active);
        var repo = new FakeStatusUserRepository(target);

        await Sut(repo, new AuditLog()).ChangeStatusAsync(target.Id, "Deactivate", Actor);

        Assert.Equal(UserStatus.Deactivated, target.Status);
    }

    [Fact]
    public async Task An_admin_is_still_deactivatable_from_here()
    {
        // The boundary the rule is drawn on. Admins are managed from this directory AND from the
        // dedicated admin routes; super admins are managed from neither. Confusing the two roles
        // would lock super admins out of administering their own admins.
        var target = UserWithRole(RoleName.Admin, UserStatus.Active);

        await Sut(new FakeStatusUserRepository(target), new AuditLog())
            .ChangeStatusAsync(target.Id, "Deactivate", Actor);

        Assert.Equal(UserStatus.Deactivated, target.Status);
    }

    // --- the bulk route -------------------------------------------------------------------------

    [Fact]
    public async Task A_batch_refuses_the_super_admin_and_applies_to_everybody_else()
    {
        // Per account, like every other outcome on this path. A batch that failed wholesale
        // because one row was protected would push an operator towards deselecting until it works,
        // which is a worse habit than a partial result with a reason on the row.
        var student = UserWithRole(RoleName.Student, UserStatus.Active);
        var admin = UserWithRole(RoleName.Admin, UserStatus.Active);
        var superAdmin = SuperAdmin(UserStatus.Active);
        var repo = new FakeStatusUserRepository(student, admin, superAdmin);

        var result = await Sut(repo, new AuditLog())
            .ChangeStatusBulkAsync([student.Id, admin.Id, superAdmin.Id], "Deactivate", Actor);

        Assert.Equal(UserStatus.Deactivated, student.Status);
        Assert.Equal(UserStatus.Deactivated, admin.Status);
        Assert.Equal(UserStatus.Active, superAdmin.Status);

        var refused = Assert.Single(result.Results.Where(i => !i.Succeeded));
        Assert.Equal(superAdmin.Id, refused.UserId);
        Assert.Contains("super administrator", refused.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Two_super_admins_cannot_disable_each_other()
    {
        // The scenario in full, and the reason the self-target guard is not enough. Neither call
        // below is a self-target; both used to succeed; after the second one nobody could sign in
        // to the super-admin surface and no route could appoint a replacement.
        var first = SuperAdmin(UserStatus.Active);
        var second = SuperAdmin(UserStatus.Active);
        var sut = Sut(new FakeStatusUserRepository(first, second), new AuditLog());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ChangeStatusAsync(second.Id, "Deactivate", first.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ChangeStatusAsync(first.Id, "Deactivate", second.Id));

        Assert.Equal(UserStatus.Active, first.Status);
        Assert.Equal(UserStatus.Active, second.Status);
    }

    [Fact]
    public async Task A_super_admin_still_cannot_target_themselves()
    {
        // The older guard, still first: self-target is reported as self-target rather than being
        // swallowed by the new one. The two refusals mean different things and an operator reading
        // the audit needs to see which happened.
        var actor = SuperAdmin(UserStatus.Active);
        var log = new AuditLog();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sut(new FakeStatusUserRepository(actor), log)
                .ChangeStatusAsync(actor.Id, "Deactivate", actor.Id));

        Assert.Contains(log.Entries, e => e.Message.Contains("SelfTarget"));
    }

    // --- the other door, which was already shut --------------------------------------------------

    [Fact]
    public void The_admin_lifecycle_query_is_still_filtered_to_admins()
    {
        // Where the intent was recorded in the first place. This finding was not "somebody forgot a
        // check" — it was that one of two routes to the same accounts carried it. If this filter is
        // ever relaxed, the rule above becomes the only thing standing, and it should be a
        // deliberate edit rather than a silent widening.
        var source = File.ReadAllText(Path.Combine(
            ServiceRoot(), "src", "UserManagementService.Infrastructure", "Persistence",
            "Repositories", "AdminRepository.cs"));

        Assert.Contains("user.Role.Name == RoleName.Admin", source);
    }

    private static string ServiceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, "src", "UserManagementService.Application")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    // --- helpers ---------------------------------------------------------------------------------

    private static UserStatusService Sut(FakeStatusUserRepository repo, AuditLog log)
        => new(repo, new RecordingStatusEventBus(), TestMapper.Create(), log);

    private static User SuperAdmin(UserStatus status) => UserWithRole(RoleName.SuperAdmin, status);

    private static User UserWithRole(RoleName role, UserStatus status)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = $"{role}".ToLowerInvariant(),
            Email = $"{role}@intellilect.io".ToLowerInvariant(),
            FirstName = "Test",
            LastName = $"{role}",
            PasswordHash = "H:pass",
            RoleId = Guid.NewGuid(),
            Role = Role.Create(role),
            RefreshTokens = [],
        };

        switch (status)
        {
            case UserStatus.Active: user.Approve(); break;
            case UserStatus.Rejected: user.Reject(); break;
            case UserStatus.Deactivated: user.Approve(); user.Deactivate(); break;
            case UserStatus.Pending: break;
        }

        return user;
    }
}
