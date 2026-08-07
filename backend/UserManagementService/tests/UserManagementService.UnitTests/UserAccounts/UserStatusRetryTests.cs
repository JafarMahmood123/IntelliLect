using IntelliLect.Contracts.Messages;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common.Users;
using UserManagementService.Application.DTOs.User;
using UserManagementService.Application.UserAccounts;
using UserManagementService.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace UserManagementService.UnitTests.UserAccounts;

/// <summary>
/// A bulk approve that is retried after a timeout (test-plan S-14).
///
/// The existing bulk suite covers the easy retry: the first attempt committed, the response was
/// lost, and the second attempt finds everything already done. What it never asks is the harder
/// one — **the first attempt did not finish.** A super admin approving fifty registrations sees a
/// gateway timeout and presses the button again, with no way to know whether the first request
/// changed nothing, everything, or half of it.
///
/// The answer rests entirely on a property nothing was asserting: **the batch is one
/// transaction.** Accounts are mutated in memory as the loop decides them, notifications are
/// queued into the outbox as it goes, and a single `SaveChangesAsync` at the end commits the lot.
/// Nothing before that line survives a failure — so "half of it" is not one of the outcomes, and
/// the retry only ever meets a batch that is entirely done or entirely undone. Every case here
/// exists to keep that true, because a later change that saved per account would leave all of
/// these passing except the ones that measure it.
///
/// **What this cannot do**, and why S-13 is still an integration row: a fake models a rolled-back
/// transaction, not an isolation level. Two requests genuinely in flight against one row is a
/// different question, answered by `User.Version` at the database and asserted at the end of this
/// file only as far as a unit test honestly can.
/// </summary>
public sealed class UserStatusRetryTests
{
    private static readonly Guid SuperAdminId = Guid.NewGuid();

    [Fact]
    public async Task A_batch_that_fails_to_commit_leaves_no_account_approved()
    {
        // The timeout landing on the commit itself. Every account was already mutated in memory
        // by the time the save ran, so "did it work?" is decided entirely by whether those
        // mutations were committed together.
        var repo = Repo(Pending(), Pending(), Pending());
        repo.FailNextSave(new TimeoutException("the database took too long"));
        var bus = new RecordingStatusEventBus();
        var sut = new UserStatusService(repo, bus, TestMapper.Create(), NullLogger<UserStatusService>.Instance);

        await Assert.ThrowsAsync<TimeoutException>(
            () => sut.ChangeStatusBulkAsync(repo.Ids, "Accept", SuperAdminId));

        Assert.All(repo.Users, u => Assert.Equal(UserStatus.Pending, u.Status));
    }

    [Fact]
    public async Task A_batch_that_fails_to_commit_sends_nobody_an_email()
    {
        // The notifications are queued into the outbox during the loop, BEFORE the commit — which
        // is what makes them atomic with it. Get that backwards, by publishing straight to the
        // broker or by saving the outbox rows separately, and a batch that failed still tells
        // fifty people they were approved when they were not. They then cannot sign in.
        //
        // Asserted as "how many had been published when the save was attempted", because the fake
        // bus cannot un-record. That number IS the outbox contents at commit time.
        var repo = Repo(Pending(), Pending());
        var bus = new RecordingStatusEventBus();
        repo.FailNextSave(new TimeoutException("the database took too long"));
        repo.PublishedSoFar = () => bus.Published.Count;
        var sut = new UserStatusService(repo, bus, TestMapper.Create(), NullLogger<UserStatusService>.Instance);

        await Assert.ThrowsAsync<TimeoutException>(
            () => sut.ChangeStatusBulkAsync(repo.Ids, "Accept", SuperAdminId));

        Assert.Equal(2, repo.PublishedCountAtSave);
    }

    [Fact]
    public async Task Retrying_after_a_failed_attempt_approves_everyone_exactly_once()
    {
        // The whole point of the row. Second press of the button: the batch is still entirely
        // pending, so it applies in full — and each account is notified once, not once per
        // attempt.
        var repo = Repo(Pending(), Pending(), Pending());
        var bus = new RecordingStatusEventBus();
        repo.FailNextSave(new TimeoutException("the database took too long"));
        repo.RollBackOutbox = () => bus.Published.Clear();
        var sut = new UserStatusService(repo, bus, TestMapper.Create(), NullLogger<UserStatusService>.Instance);

        await Assert.ThrowsAsync<TimeoutException>(
            () => sut.ChangeStatusBulkAsync(repo.Ids, "Accept", SuperAdminId));
        var retry = await sut.ChangeStatusBulkAsync(repo.Ids, "Accept", SuperAdminId);

        Assert.Equal(3, retry.Succeeded);
        Assert.All(repo.Users, u => Assert.Equal(UserStatus.Active, u.Status));
        // Three across BOTH attempts. The first attempt's three were queued into the outbox and
        // died with the transaction; only the retry's survived.
        Assert.Equal(3, bus.Published.OfType<UserStatusChangedMessage>().Count());
    }

    [Fact]
    public async Task Retrying_after_the_response_was_lost_changes_nothing_and_still_reports_success()
    {
        // The other timeout: the work committed and the answer never arrived. Every account is
        // now a no-op, and a no-op has to be reported as a SUCCESS — a retry that comes back
        // "failed: already active" would send the operator looking for a problem that does not
        // exist, and would make the button unusable.
        var repo = Repo(Pending(), Pending());
        var bus = new RecordingStatusEventBus();
        var sut = new UserStatusService(repo, bus, TestMapper.Create(), NullLogger<UserStatusService>.Instance);

        await sut.ChangeStatusBulkAsync(repo.Ids, "Accept", SuperAdminId);
        var retry = await sut.ChangeStatusBulkAsync(repo.Ids, "Accept", SuperAdminId);

        Assert.Equal(2, retry.Succeeded);
        Assert.Equal(0, retry.Failed);
        Assert.Equal(2, bus.Published.Count);     // not four
        Assert.Equal(1, repo.SaveChangesCallCount); // the retry wrote nothing
    }

    [Fact]
    public async Task A_retry_that_is_partly_already_done_finishes_the_rest_without_repeating_itself()
    {
        // The mixed retry, which is what an operator actually produces: they re-select the whole
        // page rather than working out which rows went through. The ones already approved must
        // stay silent and the rest must go through — in one pass, with the batch still atomic.
        var alreadyDone = Pending();
        var stillWaiting = Pending();
        var repo = Repo(alreadyDone, stillWaiting);
        var bus = new RecordingStatusEventBus();
        var sut = new UserStatusService(repo, bus, TestMapper.Create(), NullLogger<UserStatusService>.Instance);

        await sut.ChangeStatusBulkAsync(new[] { alreadyDone.Id }, "Accept", SuperAdminId);
        bus.Published.Clear();

        var retry = await sut.ChangeStatusBulkAsync(repo.Ids, "Accept", SuperAdminId);

        Assert.Equal(2, retry.Succeeded);
        Assert.Equal(UserStatus.Active, stillWaiting.Status);
        var notified = Assert.Single(bus.Published.OfType<UserStatusChangedMessage>());
        Assert.Equal(stillWaiting.Email, notified.Email);
    }

    [Fact]
    public async Task A_rejection_that_fails_to_commit_leaves_the_sessions_alive()
    {
        // Rejection revokes refresh tokens in the same unit of work. A revocation that survived a
        // failed commit would be the mirror image of the A-08 defect: the account still works,
        // but its owner is signed out and cannot tell why — and no record of the rejection exists
        // to explain it.
        var user = Pending(ActiveToken(), ActiveToken());
        var repo = Repo(user);
        repo.FailNextSave(new TimeoutException("the database took too long"));
        var sut = new UserStatusService(repo, new RecordingStatusEventBus(), TestMapper.Create(), NullLogger<UserStatusService>.Instance);

        await Assert.ThrowsAsync<TimeoutException>(
            () => sut.ChangeStatusBulkAsync(repo.Ids, "Reject", SuperAdminId));

        Assert.All(user.RefreshTokens, t => Assert.False(t.IsRevoked));
        Assert.Equal(UserStatus.Pending, user.Status);
    }

    [Fact]
    public async Task A_batch_of_pure_no_ops_does_not_open_a_transaction_at_all()
    {
        // The retry's cheapest possible outcome, and the reason the save is guarded on a count
        // rather than run unconditionally. Fifty already-approved accounts should cost one read.
        // It also matters for the racing retry below: a write issued for a batch that changes
        // nothing would still roll User.Version and could still lose a concurrency check.
        var repo = Repo(Approved(), Approved(), Approved());
        var sut = new UserStatusService(repo, new RecordingStatusEventBus(), TestMapper.Create(), NullLogger<UserStatusService>.Instance);

        var result = await sut.ChangeStatusBulkAsync(repo.Ids, "Accept", SuperAdminId);

        Assert.Equal(3, result.Succeeded);
        Assert.Equal(0, repo.SaveChangesCallCount);
    }

    [Fact]
    public void Each_transition_rolls_the_row_version_that_a_racing_retry_collides_with()
    {
        // The part a fake cannot reach, stated as far as it honestly can be.
        //
        // A timeout does not stop the server. The retry may arrive while the original is still
        // running, both having read the same rows — and nothing in the service prevents that,
        // because nothing in the service can. What prevents it is User.Version being a
        // concurrency token: both attempts issue their UPDATE against the version they read, the
        // second matches no rows, and EF raises DbUpdateConcurrencyException. So the duplicate
        // approval is refused by the database and the duplicate email is never committed.
        //
        // That only works if every transition actually changes Version. This asserts the half
        // that lives in the entity; the half that lives in the DbContext configuration is
        // MigrationConformanceTests' snapshot, and the behaviour under real isolation is S-13.
        var transitions = new (string Name, Action<User> Apply)[]
        {
            ("Approve", u => u.Approve()),
            ("Reject", u => u.Reject()),
            ("Deactivate", u => { u.Approve(); u.Deactivate(); }),
            ("Reactivate", u => { u.Approve(); u.Deactivate(); u.Reactivate(); }),
        };

        foreach (var (name, apply) in transitions)
        {
            var user = Pending();
            var before = user.Version;

            apply(user);

            Assert.True(user.Version != before, $"{name} left Version unchanged");
        }
    }

    // --- helpers ------------------------------------------------------------------------

    private static RollbackAwareUserRepository Repo(params User[] users) => new(users);

    private static RefreshToken ActiveToken() => new()
    {
        Id = Guid.NewGuid(),
        Token = Guid.NewGuid().ToString("N"),
        IsRevoked = false,
        ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
    };

    private static User Approved()
    {
        var user = Pending();
        user.Approve();
        return user;
    }

    private static User Pending(params RefreshToken[] tokens) => new()
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
}

/// <summary>
/// A user table that can lose a transaction.
///
/// The other fakes in this suite hand back the same instances the service mutated, so a failed
/// save leaves those mutations visible — the opposite of what a database does, and it would make
/// every retry case here pass or fail for the wrong reason. This one records each account's state
/// when it is read and restores it if the save throws, which is what the next request would find.
///
/// It models a rollback and NOTHING ELSE. It has no isolation, no locking, and no notion of a
/// second connection; the racing-retry question belongs to S-13 and an integration suite.
/// </summary>
internal sealed class RollbackAwareUserRepository : IUserRepository
{
    private readonly List<User> _users;
    private List<Snapshot> _readInThisAttempt = [];
    private Exception? _failNextSave;

    public RollbackAwareUserRepository(params User[] users) => _users = users.ToList();

    public IReadOnlyList<User> Users => _users;
    public Guid[] Ids => _users.Select(u => u.Id).ToArray();

    public int SaveChangesCallCount { get; private set; }

    /// <summary>How many messages the bus had seen when the save was attempted.</summary>
    public int PublishedCountAtSave { get; private set; } = -1;
    public Func<int> PublishedSoFar { get; set; } = () => 0;

    public void FailNextSave(Exception failure) => _failNextSave = failure;

    /// <summary>
    /// Discards the outbox rows queued during the failed attempt.
    ///
    /// Not a convenience. The outbox lives in the SAME DbContext as the user rows and commits in
    /// the same `SaveChangesAsync`, so a rollback takes the queued notifications with it — that
    /// is the entire reason the service publishes into the outbox instead of straight to the
    /// broker. A fake that rolled back the accounts but kept the messages would model a system
    /// where a failed batch still emails everyone it did not approve.
    /// </summary>
    public Action? RollBackOutbox { get; set; }

    public Task<List<User>> GetByIdsWithRefreshTokensAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var loaded = _users.Where(u => ids.Contains(u.Id)).ToList();
        _readInThisAttempt = loaded.Select(Snapshot.Of).ToList();
        return Task.FromResult(loaded);
    }

    public Task<User?> GetByIdWithRefreshTokensAsync(Guid id, CancellationToken ct = default)
    {
        var found = _users.FirstOrDefault(u => u.Id == id);
        _readInThisAttempt = found is null ? [] : [Snapshot.Of(found)];
        return Task.FromResult(found);
    }

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        PublishedCountAtSave = PublishedSoFar();
        SaveChangesCallCount++;

        if (_failNextSave is { } failure)
        {
            _failNextSave = null;
            foreach (var snapshot in _readInThisAttempt) snapshot.Restore();
            RollBackOutbox?.Invoke();
            throw failure;
        }

        return Task.FromResult(1);
    }

    /// <summary>What one account looked like before the attempt touched it.</summary>
    private sealed record Snapshot(User User, UserStatus Status, Guid Version, Guid[] RevokedTokenIds)
    {
        public static Snapshot Of(User user) => new(
            user,
            user.Status,
            user.Version,
            user.RefreshTokens.Where(t => t.IsRevoked).Select(t => t.Id).ToArray());

        public void Restore()
        {
            // Status and Version have private setters and no "set it back" transition, which is
            // correct for the domain — so the rollback goes through the same reflection a
            // persistence layer would use to rehydrate a row.
            typeof(User).GetProperty(nameof(User.Status))!.SetValue(User, Status);
            typeof(User).GetProperty(nameof(User.Version))!.SetValue(User, Version);

            foreach (var token in User.RefreshTokens.Where(t => !RevokedTokenIds.Contains(t.Id)))
            {
                token.IsRevoked = false;
            }
        }
    }

    public Task<User?> FindByEmail(string email, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<User?> FindByRefreshToken(string token, CancellationToken ct) => throw new NotImplementedException();
    public Task<User?> FindByResetToken(string token, CancellationToken ct) => throw new NotImplementedException();
    public Task<List<User>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<(List<User> Items, int TotalCount)> GetPaginatedPendingUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct) => throw new NotImplementedException();
    public Task<(List<User> Items, int TotalCount)> GetPaginatedUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct) => throw new NotImplementedException();
    public Task<(List<User> Items, int TotalCount)> SearchUsersAsync(UserQuerySpecification specification, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task AddAsync(User entity, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpdateAsync(User entity, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
}
