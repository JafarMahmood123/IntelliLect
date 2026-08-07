using Microsoft.Extensions.Logging;
using UserManagementService.Application.Common.Users;
using UserManagementService.Application.UserAccounts;
using UserManagementService.Domain.Entities;

namespace UserManagementService.UnitTests.UserAccounts;

/// <summary>
/// The audit trail records what HAPPENED, not what was attempted (test-plan C-09, C-11).
///
/// C-10 and C-11 were the last two rows in the plan marked `new`. Reading the suite, both were
/// already covered and the markers were stale — `UserStatusBulkServiceTests` proves N changed
/// accounts produce N notifications and no more, and `UserStatusRetryTests` proves the whole batch
/// is one transaction, so a delivery failure cannot undo an approval and a failed commit cannot
/// leave half of one. Corrected in the plan rather than re-tested here.
///
/// **What reading them together turned up is a defect neither one could see.** They are both true,
/// and the audit added in C-09 was written in the wrong place relative to them:
///
///   * `A_batch_that_fails_to_commit_leaves_no_account_approved` — nothing is persisted;
///   * `RecordAudit` ran inside the decision loop, **before** the commit that makes it true.
///
/// So a bulk approve of fifty registrations that timed out on its commit wrote fifty Information
/// lines saying fifty accounts had been approved, approved none of them, and wrote nothing at all
/// to say it had failed. Each line was indistinguishable from a real one.
///
/// That is the worst direction for this log to be wrong in. C-09 exists because deciding who may
/// sign in is the most privileged thing anyone does in this product, and the question the log is
/// asked is "was this person's account deactivated, by whom, and when". After a rolled-back batch
/// it answered **yes** when the truth was **no** — and the operator, having seen an error, has no
/// way to tell which of the two the log is describing.
///
/// A refusal is different and stays where it was: nothing about it depends on a transaction, and
/// it is exactly as true when the rest of the batch fails.
/// </summary>
public sealed class UserStatusAuditOutcomeTests
{
    private static readonly Guid SuperAdminId = Guid.NewGuid();

    // --- the defect ----------------------------------------------------------------------------

    [Fact]
    public async Task A_batch_that_fails_to_commit_claims_no_approvals()
    {
        var repo = Repo(Pending(), Pending(), Pending());
        repo.FailNextSave(new TimeoutException("the database took too long"));
        var log = new AuditLog();
        var sut = Sut(repo, log);

        await Assert.ThrowsAsync<TimeoutException>(
            () => sut.ChangeStatusBulkAsync(repo.Ids, "Accept", SuperAdminId));

        // Not "fewer than three" — none. Every account is still Pending, so any line asserting a
        // change is a line describing something that did not happen.
        Assert.DoesNotContain(log.Entries, entry => entry.Level == LogLevel.Information);
        Assert.All(repo.Users, user => Assert.Equal(UserStatus.Pending, user.Status));
    }

    [Fact]
    public async Task A_batch_that_fails_to_commit_says_so_and_names_the_accounts()
    {
        // The other half. Silence would be an improvement on a false claim and is still not
        // enough: an operator who saw the request fail needs to know which accounts to look at,
        // and the log is the only place that survives the request.
        var repo = Repo(Pending(), Pending(), Pending());
        repo.FailNextSave(new TimeoutException("the database took too long"));
        var log = new AuditLog();
        var sut = Sut(repo, log);

        await Assert.ThrowsAsync<TimeoutException>(
            () => sut.ChangeStatusBulkAsync(repo.Ids, "Accept", SuperAdminId));

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(SuperAdminId.ToString(), entry.Message);
        Assert.Contains("Accept", entry.Message);
        foreach (var user in repo.Users)
        {
            Assert.Contains(user.Id.ToString(), entry.Message);
        }
        // The exception rides along, so whoever reads this can tell a timeout from a concurrency
        // conflict without correlating two log lines.
        Assert.IsType<TimeoutException>(entry.Error);
    }

    [Fact]
    public async Task A_single_change_that_fails_to_commit_claims_nothing_and_reports_the_failure()
    {
        // The same defect on the single-account path, which had the identical ordering.
        var user = Pending();
        var repo = Repo(user);
        repo.FailNextSave(new TimeoutException("the database took too long"));
        var log = new AuditLog();
        var sut = Sut(repo, log);

        await Assert.ThrowsAsync<TimeoutException>(
            () => sut.ChangeStatusAsync(user.Id, "Accept", SuperAdminId));

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(user.Id.ToString(), entry.Message);
        Assert.Equal(UserStatus.Pending, user.Status);
    }

    [Fact]
    public async Task A_refusal_in_a_failed_batch_is_still_recorded()
    {
        // Deliberately NOT deferred. A refusal is final at the moment it is decided — no
        // transaction makes it more or less true — and a super admin being refused is the thing
        // this log most needs to keep. Moving every record behind the commit would have thrown it
        // away along with the false claims.
        var pending = Pending();
        var alreadyRejected = Rejected();
        var repo = Repo(pending, alreadyRejected);
        repo.FailNextSave(new TimeoutException("the database took too long"));
        var log = new AuditLog();
        var sut = Sut(repo, log);

        await Assert.ThrowsAsync<TimeoutException>(
            () => sut.ChangeStatusBulkAsync(repo.Ids, "Accept", SuperAdminId));

        Assert.Contains(
            log.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains(alreadyRejected.Id.ToString()));
        Assert.DoesNotContain(log.Entries, entry => entry.Level == LogLevel.Information);
    }

    // --- what must not have changed while fixing it --------------------------------------------

    [Fact]
    public async Task A_batch_that_commits_still_records_one_line_per_account()
    {
        // The regression guard on the reordering. Deferring the records must not lose them, and
        // must not collapse fifty into one — C-09's whole point is that "a super admin changed 50
        // accounts" cannot answer a question about one of them.
        var repo = Repo(Pending(), Pending(), Pending());
        var log = new AuditLog();
        var sut = Sut(repo, log);

        await sut.ChangeStatusBulkAsync(repo.Ids, "Accept", SuperAdminId);

        Assert.Equal(3, log.Entries.Count);
        Assert.All(log.Entries, entry => Assert.Equal(LogLevel.Information, entry.Level));
        foreach (var user in repo.Users)
        {
            Assert.Contains(log.Entries, entry => entry.Message.Contains(user.Id.ToString()));
        }
    }

    [Fact]
    public async Task The_recorded_status_is_the_one_the_account_ended_up_in()
    {
        // Holding the records back means holding the status they report, and reading it later
        // from a mutated entity is an easy way to record the wrong one. Reject and Deactivate in
        // one batch, so a single shared value could not satisfy both.
        var toReject = Pending();
        var toDeactivate = Approved();
        var log = new AuditLog();
        var sut = Sut(Repo(toReject), log);

        await sut.ChangeStatusBulkAsync([toReject.Id], "Reject", SuperAdminId);
        var rejected = Assert.Single(log.Entries);
        Assert.Contains("Rejected", rejected.Message);

        var secondLog = new AuditLog();
        var secondSut = Sut(Repo(toDeactivate), secondLog);
        await secondSut.ChangeStatusBulkAsync([toDeactivate.Id], "Deactivate", SuperAdminId);
        var deactivated = Assert.Single(secondLog.Entries);
        Assert.Contains("Deactivated", deactivated.Message);
    }

    [Fact]
    public async Task The_failure_record_identifies_accounts_by_id_and_never_by_email_or_name()
    {
        // A-24's rule, applied to the new sink. The failure line is the one most likely to be
        // pasted into a ticket, which makes it the worst place to accumulate a roster.
        var user = Pending();
        var repo = Repo(user);
        repo.FailNextSave(new TimeoutException("the database took too long"));
        var log = new AuditLog();
        var sut = Sut(repo, log);

        await Assert.ThrowsAsync<TimeoutException>(
            () => sut.ChangeStatusBulkAsync(repo.Ids, "Accept", SuperAdminId));

        var entry = Assert.Single(log.Entries);
        Assert.DoesNotContain(user.Email, entry.Message);
        Assert.DoesNotContain(user.FirstName, entry.Message);
        Assert.DoesNotContain(user.LastName, entry.Message);
    }

    [Fact]
    public async Task A_batch_of_pure_no_ops_records_nothing_and_never_reaches_a_commit()
    {
        // No commit means no opportunity for a commit-failure record either, and a retried page of
        // already-approved accounts must stay silent — otherwise the retry buries the batch it is
        // retrying.
        var repo = Repo(Approved(), Approved());
        var log = new AuditLog();
        var sut = Sut(repo, log);

        await sut.ChangeStatusBulkAsync(repo.Ids, "Accept", SuperAdminId);

        Assert.Empty(log.Entries);
        Assert.Equal(0, repo.SaveChangesCallCount);
    }

    // --- helpers --------------------------------------------------------------------------------

    private static UserStatusService Sut(RollbackAwareUserRepository repo, AuditLog log)
        => new(repo, new RecordingStatusEventBus(), TestMapper.Create(), log);

    private static RollbackAwareUserRepository Repo(params User[] users) => new(users);

    private static User Approved()
    {
        var user = Pending();
        user.Approve();
        return user;
    }

    private static User Rejected()
    {
        var user = Pending();
        user.Reject();
        return user;
    }

    private static User Pending() => new()
    {
        Id = Guid.NewGuid(),
        UserName = "zeynab",
        Email = "zeynab@intellilect.io",
        FirstName = "Zeynab",
        LastName = "Karimi",
        PasswordHash = "H:pass",
        RoleId = Guid.NewGuid(),
        Role = Role.Create(RoleName.Student),
        RefreshTokens = [],
    };
}
