using AutoMapper;
using IntelliLect.Contracts.Messages;
using UserManagementService.Application.Common.Users;
using UserManagementService.Application.UserAccounts;
using UserManagementService.Domain.Entities;

namespace UserManagementService.UnitTests.UserAccounts;

/// <summary>
/// Bulk status changes (test-plan C-03 … C-12).
///
/// The behaviour under test is partial success: one bad id must not sink the rest, every id must
/// be reported, and the per-account rules must be identical to the single-account path.
/// </summary>
public class UserStatusBulkServiceTests
{
    private static readonly Guid SuperAdminId = Guid.NewGuid();

    // --- C-03 / C-04: per-item results, one failure does not sink the batch ------

    [Fact]
    public async Task Bulk_MixedBatch_AppliesTheValidOnesAndNamesTheFailures()
    {
        var pendingA = UserWith(UserStatus.Pending);
        var pendingB = UserWith(UserStatus.Pending);
        var alreadyRejected = UserWith(UserStatus.Rejected);
        var unknownId = Guid.NewGuid();

        var repo = new FakeStatusUserRepository(pendingA, pendingB, alreadyRejected);
        var bus = new RecordingStatusEventBus();
        var sut = CreateSut(repo, bus);

        var result = await sut.ChangeStatusBulkAsync(
            new[] { pendingA.Id, unknownId, pendingB.Id, alreadyRejected.Id }, "Accept", SuperAdminId);

        // The two valid approvals happened despite the unknown id and the invalid transition.
        Assert.Equal(UserStatus.Active, pendingA.Status);
        Assert.Equal(UserStatus.Active, pendingB.Status);

        Assert.Equal(4, result.Requested);
        Assert.Equal(2, result.Succeeded);
        Assert.Equal(2, result.Failed);

        Assert.Equal("Account not found.", Item(result, unknownId).Error);
        Assert.Contains("Rejected", Item(result, alreadyRejected.Id).Error);
        Assert.Equal(UserStatus.Rejected, alreadyRejected.Status); // untouched
    }

    [Fact]
    public async Task Bulk_EveryRequestedId_AppearsInTheResults()
    {
        var user = UserWith(UserStatus.Pending);
        var missing = Guid.NewGuid();
        var repo = new FakeStatusUserRepository(user);
        var sut = CreateSut(repo, new RecordingStatusEventBus());

        var result = await sut.ChangeStatusBulkAsync(new[] { user.Id, missing }, "Accept", SuperAdminId);

        Assert.Equal(2, result.Results.Count);
        Assert.Contains(result.Results, r => r.UserId == user.Id);
        Assert.Contains(result.Results, r => r.UserId == missing);
    }

    // --- C-05: authorization is per account, not once for the batch -------------

    [Fact]
    public async Task Bulk_SelfTargeting_FailsOnlyThatAccount()
    {
        // The super admin's own id is in the selection. Their account must be refused while
        // everyone else is still processed.
        var other = UserWith(UserStatus.Pending);
        var self = UserWith(UserStatus.Pending);
        var repo = new FakeStatusUserRepository(other, self);
        var sut = CreateSut(repo, new RecordingStatusEventBus());

        var result = await sut.ChangeStatusBulkAsync(
            new[] { other.Id, self.Id }, "Accept", requestingSuperAdminId: self.Id);

        Assert.Equal(UserStatus.Active, other.Status);
        Assert.Equal(UserStatus.Pending, self.Status);
        Assert.True(Item(result, other.Id).Succeeded);
        Assert.False(Item(result, self.Id).Succeeded);
        Assert.Contains("your own account", Item(result, self.Id).Error);
    }

    // --- C-06 / C-12: idempotency ----------------------------------------------

    [Fact]
    public async Task Bulk_AlreadyInTargetState_IsASuccessfulNoOp_WithNoSecondNotification()
    {
        var alreadyActive = UserWith(UserStatus.Active);
        var repo = new FakeStatusUserRepository(alreadyActive);
        var bus = new RecordingStatusEventBus();
        var sut = CreateSut(repo, bus);

        var result = await sut.ChangeStatusBulkAsync(new[] { alreadyActive.Id }, "Accept", SuperAdminId);

        // Reported as succeeded — a retried request must not look like a failure.
        Assert.True(Item(result, alreadyActive.Id).Succeeded);
        Assert.Equal(1, result.Succeeded);
        // ...but nothing was written and nobody was emailed a second time.
        Assert.Empty(bus.Published);
        Assert.Equal(0, repo.SaveChangesCallCount);
    }

    [Fact]
    public async Task Bulk_ReplayingTheSameBatch_ChangesNothingTheSecondTime()
    {
        var user = UserWith(UserStatus.Pending);
        var repo = new FakeStatusUserRepository(user);
        var bus = new RecordingStatusEventBus();
        var sut = CreateSut(repo, bus);

        await sut.ChangeStatusBulkAsync(new[] { user.Id }, "Accept", SuperAdminId);
        var second = await sut.ChangeStatusBulkAsync(new[] { user.Id }, "Accept", SuperAdminId);

        Assert.True(Item(second, user.Id).Succeeded);
        Assert.Single(bus.Published);            // one notification in total, not two
        Assert.Equal(1, repo.SaveChangesCallCount);
    }

    [Fact]
    public async Task Bulk_DuplicateIdsInOneRequest_AreCollapsed()
    {
        var user = UserWith(UserStatus.Pending);
        var repo = new FakeStatusUserRepository(user);
        var bus = new RecordingStatusEventBus();
        var sut = CreateSut(repo, bus);

        var result = await sut.ChangeStatusBulkAsync(
            new[] { user.Id, user.Id, user.Id }, "Accept", SuperAdminId);

        Assert.Equal(1, result.Requested);
        Assert.Single(bus.Published);
    }

    // --- C-07 / C-08: request-level guards --------------------------------------

    [Fact]
    public async Task Bulk_EmptySelection_IsRefused_NotTreatedAsEveryone()
    {
        var sut = CreateSut(new FakeStatusUserRepository(), new RecordingStatusEventBus());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.ChangeStatusBulkAsync(Array.Empty<Guid>(), "Accept", SuperAdminId));
    }

    [Fact]
    public async Task Bulk_OverTheCap_IsRefused()
    {
        var tooMany = Enumerable.Range(0, UserStatusService.MaxBulkSize + 1)
            .Select(_ => Guid.NewGuid())
            .ToArray();
        var sut = CreateSut(new FakeStatusUserRepository(), new RecordingStatusEventBus());

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.ChangeStatusBulkAsync(tooMany, "Accept", SuperAdminId));

        Assert.Contains(UserStatusService.MaxBulkSize.ToString(), ex.Message);
    }

    [Fact]
    public async Task Bulk_UnknownAction_IsRefusedBeforeAnythingIsLoaded()
    {
        var repo = new FakeStatusUserRepository(UserWith(UserStatus.Pending));
        var sut = CreateSut(repo, new RecordingStatusEventBus());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.ChangeStatusBulkAsync(new[] { Guid.NewGuid() }, "Obliterate", SuperAdminId));

        Assert.Equal(0, repo.BulkLoadCallCount);
    }

    // --- C-10 / C-11: notifications and transaction shape -----------------------

    [Fact]
    public async Task Bulk_NotifiesOncePerChangedAccount_InOneTransaction()
    {
        var a = UserWith(UserStatus.Pending);
        var b = UserWith(UserStatus.Pending);
        var c = UserWith(UserStatus.Pending);
        var repo = new FakeStatusUserRepository(a, b, c);
        var bus = new RecordingStatusEventBus();
        var sut = CreateSut(repo, bus);

        await sut.ChangeStatusBulkAsync(new[] { a.Id, b.Id, c.Id }, "Accept", SuperAdminId);

        // One message per user — the mail fan-out is per account, not per batch.
        Assert.Equal(3, bus.Published.OfType<UserStatusChangedMessage>().Count());
        // ...but a single load and a single commit: N accounts must not mean N round trips.
        Assert.Equal(1, repo.BulkLoadCallCount);
        Assert.Equal(1, repo.SaveChangesCallCount);
    }

    [Fact]
    public async Task Bulk_RejectRevokesSessions_ForEveryRejectedAccount()
    {
        var a = UserWith(UserStatus.Pending, ActiveToken(), ActiveToken());
        var b = UserWith(UserStatus.Pending, ActiveToken());
        var repo = new FakeStatusUserRepository(a, b);
        var sut = CreateSut(repo, new RecordingStatusEventBus());

        await sut.ChangeStatusBulkAsync(new[] { a.Id, b.Id }, "Reject", SuperAdminId);

        Assert.All(a.RefreshTokens, t => Assert.True(t.IsRevoked));
        Assert.All(b.RefreshTokens, t => Assert.True(t.IsRevoked));
    }

    // --- the rules must match the single-account path ---------------------------

    [Fact]
    public async Task Bulk_AndSingle_AgreeOnAnInvalidTransition()
    {
        // Same account, same action, one through each entry point: both must refuse.
        var rejected = UserWith(UserStatus.Rejected);
        var singleRepo = new FakeStatusUserRepository(rejected);
        var single = CreateSut(singleRepo, new RecordingStatusEventBus());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            single.ChangeStatusAsync(rejected.Id, "Accept", SuperAdminId));

        var bulkRepo = new FakeStatusUserRepository(rejected);
        var bulk = CreateSut(bulkRepo, new RecordingStatusEventBus());
        var result = await bulk.ChangeStatusBulkAsync(new[] { rejected.Id }, "Accept", SuperAdminId);

        Assert.False(Item(result, rejected.Id).Succeeded);
        Assert.Equal(UserStatus.Rejected, rejected.Status);
    }

    // ----- helpers -------------------------------------------------------------

    private static UserStatusService CreateSut(FakeStatusUserRepository repo, RecordingStatusEventBus bus)
        => new(repo, bus, new MapperConfiguration(cfg => cfg.AddProfile<UserProfile>()).CreateMapper());

    private static UserManagementService.Application.DTOs.User.BulkUserStatusItem Item(
        UserManagementService.Application.DTOs.User.BulkUserStatusResult result, Guid userId)
        => result.Results.Single(r => r.UserId == userId);

    private static RefreshToken ActiveToken() => new()
    {
        Id = Guid.NewGuid(),
        Token = Guid.NewGuid().ToString("N"),
        IsRevoked = false,
        ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
    };

    private static User UserWith(UserStatus status, params RefreshToken[] tokens)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = "user",
            Email = $"{Guid.NewGuid():N}@intellilect.io",
            FirstName = "First",
            LastName = "Last",
            PasswordHash = "H:pass",
            RoleId = Guid.NewGuid(),
            Role = Role.Create(RoleName.Student),
            RefreshTokens = tokens.ToList(),
        };
        switch (status)
        {
            case UserStatus.Active: user.Approve(); break;
            case UserStatus.Rejected: user.Reject(); break;
            case UserStatus.Deactivated: user.Deactivate(); break;
            case UserStatus.Pending: break;
        }
        return user;
    }
}
