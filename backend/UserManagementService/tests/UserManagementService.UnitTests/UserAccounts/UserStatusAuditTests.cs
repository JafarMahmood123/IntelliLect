using Microsoft.Extensions.Logging;
using UserManagementService.Application.Common.Users;
using UserManagementService.Application.UserAccounts;
using UserManagementService.Domain.Entities;

namespace UserManagementService.UnitTests.UserAccounts;

/// <summary>
/// Who changed whose account, and when (test-plan C-09).
///
/// C-09 asks for "one audit record per user, not one per batch". It was marked `new`, and the
/// reason turned out to be that there was no record of any kind. **`UserStatusService` had no
/// logger.** Approving, rejecting and deactivating accounts — deciding who may sign in to the
/// system at all, and the most privileged thing a super admin can do — wrote nothing anywhere.
///
/// The comparison that makes it obvious is inside this repository: ClassroomService writes an
/// "Audit accountability: who downloaded which file, when" line for every recording anyone
/// downloads. Reading a lecture recording was accounted for. Deactivating somebody's account
/// was not.
///
/// The per-user half of the row is the part with teeth. "A super admin changed 50 accounts"
/// cannot answer the only question an audit trail is ever asked — *was this person's account
/// deactivated, by whom, and when* — so a batch that writes one line is the same as writing
/// none, for anyone trying to find out.
/// </summary>
public sealed class UserStatusAuditTests
{
    private static readonly Guid SuperAdminId = Guid.NewGuid();

    [Fact]
    public async Task Changing_one_account_records_who_did_it_and_what_changed()
    {
        var user = UserWith(UserStatus.Pending);
        var log = new AuditLog();
        var sut = Sut(new FakeStatusUserRepository(user), log);

        await sut.ChangeStatusAsync(user.Id, "Accept", SuperAdminId);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(SuperAdminId.ToString(), entry.Message);
        Assert.Contains(user.Id.ToString(), entry.Message);
        Assert.Contains("Accept", entry.Message);
        Assert.Contains("Active", entry.Message);
    }

    [Fact]
    public async Task A_batch_writes_one_record_per_account_not_one_for_the_batch()
    {
        // The row, stated exactly. Three accounts, three records, each naming its own id — so a
        // later question about ONE of them can be answered without knowing which batch it was in.
        var users = new[] { UserWith(UserStatus.Pending), UserWith(UserStatus.Pending), UserWith(UserStatus.Pending) };
        var log = new AuditLog();
        var sut = Sut(new FakeStatusUserRepository(users), log);

        await sut.ChangeStatusBulkAsync(users.Select(u => u.Id).ToArray(), "Accept", SuperAdminId);

        Assert.Equal(3, log.Entries.Count);
        foreach (var user in users)
        {
            Assert.Contains(log.Entries, e => e.Message.Contains(user.Id.ToString()));
        }
    }

    [Fact]
    public async Task A_refusal_is_recorded_as_a_warning()
    {
        // A run of refusals is somebody trying to do something they may not, which is precisely
        // what an audit trail exists to make visible. Recording only the successes would keep
        // the attempts invisible.
        var user = UserWith(UserStatus.Rejected);
        var log = new AuditLog();
        var sut = Sut(new FakeStatusUserRepository(user), log);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ChangeStatusAsync(user.Id, "Accept", SuperAdminId));

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(user.Id.ToString(), entry.Message);
    }

    [Fact]
    public async Task Targeting_your_own_account_is_recorded()
    {
        // The one refusal worth naming on its own: a super admin cannot change their own status,
        // and an attempt to is the single most interesting line this log could ever contain.
        var log = new AuditLog();
        var sut = Sut(new FakeStatusUserRepository(user: null), log);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ChangeStatusAsync(SuperAdminId, "Deactivate", SuperAdminId));

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("SelfTarget", entry.Message);
    }

    [Fact]
    public async Task A_no_op_records_nothing()
    {
        // What a retried request looks like. Recording it would bury the changes that happened
        // under repeats of the ones that did not — and a retry after a timeout produces a whole
        // page of them at once.
        var user = UserWith(UserStatus.Active);
        var log = new AuditLog();
        var sut = Sut(new FakeStatusUserRepository(user), log);

        await sut.ChangeStatusAsync(user.Id, "Accept", SuperAdminId);

        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task A_mixed_batch_records_each_account_with_its_own_outcome()
    {
        // Both kinds in one batch, which is the realistic case: the operator selected a page and
        // some of it was not actionable. Each account's line has to reflect that account.
        var accepted = UserWith(UserStatus.Pending);
        var refused = UserWith(UserStatus.Rejected);
        var noOp = UserWith(UserStatus.Active);
        var log = new AuditLog();
        var sut = Sut(new FakeStatusUserRepository(accepted, refused, noOp), log);

        await sut.ChangeStatusBulkAsync(
            [accepted.Id, refused.Id, noOp.Id], "Accept", SuperAdminId);

        Assert.Equal(2, log.Entries.Count);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains(accepted.Id.ToString()));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(refused.Id.ToString()));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains(noOp.Id.ToString()));
    }

    [Fact]
    public async Task The_record_identifies_accounts_by_id_and_never_by_email_or_name()
    {
        // A-24's rule, applied to a different sink. A log file is not a place to accumulate a
        // roster of who is registered here — and the id is what any follow-up query needs anyway,
        // so the address adds exposure and nothing else.
        var user = UserWith(UserStatus.Pending);
        var log = new AuditLog();
        var sut = Sut(new FakeStatusUserRepository(user), log);

        await sut.ChangeStatusAsync(user.Id, "Reject", SuperAdminId);

        var entry = Assert.Single(log.Entries);
        Assert.DoesNotContain(user.Email, entry.Message);
        Assert.DoesNotContain(user.FirstName, entry.Message);
        Assert.DoesNotContain(user.LastName, entry.Message);
    }

    // --- helpers ------------------------------------------------------------------------

    private static UserStatusService Sut(FakeStatusUserRepository repo, AuditLog log)
        => new(repo, new RecordingStatusEventBus(), TestMapper.Create(), log);

    private static User UserWith(UserStatus status)
    {
        var user = new User
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

/// <summary>
/// Keeps level and rendered text, because both halves of the rule are about them.
///
/// Top-level rather than nested so `UserStatusAuditOutcomeTests` shares it: one double for one
/// sink, so a change to what the audit records cannot be half-adopted by one of the two files.
/// </summary>
internal sealed class AuditLog : ILogger<UserStatusService>
{
    public List<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;

    public void Log<TState>(
        LogLevel level, EventId id, TState state, Exception? error,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((level, formatter(state, error), error));
}
