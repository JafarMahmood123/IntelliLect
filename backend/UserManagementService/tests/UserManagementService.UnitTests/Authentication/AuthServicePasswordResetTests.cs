using IntelliLect.Contracts.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UserManagementService.Application.DTOs.Auth;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Authentication;
using UserManagementService.Domain.Entities;

namespace UserManagementService.UnitTests.Authentication;

/// <summary>
/// Password reset — test-plan A-08, and until now untested end to end.
///
/// `ForgotPasswordAsync` had two cases; `ResetPasswordAsync`, the half that actually changes a
/// credential, had none. It is the most attractive endpoint in the system: it hands out a
/// short-lived secret over email and takes it back in exchange for a new password, so every
/// property it rests on — single use, expiry, rate limiting, and what happens to the sessions
/// that existed before — is load-bearing.
///
/// Two of them were wrong.
/// </summary>
public sealed class AuthServicePasswordResetTests
{
    private const string Email = "amina@intellilect.io";
    private const string Code = "reset-code-000000";  // what FixedResetTokenGenerator emits

    // --- issuing a code ---------------------------------------------------------------

    [Fact]
    public async Task The_reset_code_is_never_written_to_a_log()
    {
        // It used to be, in plaintext beside the email address, through Console.WriteLine — which
        // bypasses log-level filtering entirely and still lands in Serilog's file sink. Anyone
        // able to read a log file could take over any account: ask for a reset, read the code,
        // never touch the mailbox.
        var log = new CapturingLogger();
        var harness = new ResetHarness(log);

        await harness.Sut.ForgotPasswordAsync(Email, default);

        Assert.NotEmpty(harness.Bus.Published.OfType<SendResetCodeMessage>());  // it was still sent
        Assert.DoesNotContain(log.Messages, message => message.Contains(Code));
        Assert.DoesNotContain(log.Messages, message => message.Contains(Email));
    }

    [Fact]
    public async Task The_stored_token_is_not_the_code_itself()
    {
        // A reset table holding live codes turns a read-only database leak into account takeover
        // for every user who has ever asked for one.
        var harness = new ResetHarness();

        await harness.Sut.ForgotPasswordAsync(Email, default);

        var stored = harness.ResetTokens.Added!;
        Assert.NotEqual(Code, stored.Token);
    }

    [Fact]
    public async Task A_sixth_request_inside_a_day_is_refused()
    {
        // Without a cap this endpoint is an email cannon pointed at any address an attacker
        // knows, and a way to bury a victim's inbox in codes they did not ask for.
        var harness = new ResetHarness();
        harness.ResetTokens.Seed(TokenFor(harness.User, requestCount: 5, lastRequested: DateTime.UtcNow));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Sut.ForgotPasswordAsync(Email, default));

        Assert.Empty(harness.Bus.Published.OfType<SendResetCodeMessage>());
    }

    [Fact]
    public async Task The_daily_count_starts_again_once_the_day_has_passed()
    {
        // The cap is per day, not per lifetime. A user who exhausted it last week must not be
        // permanently unable to recover their own account.
        //
        // Asserting only that this ONE request succeeded is not enough, and mutation testing
        // proved it: with the counter reset removed, this request still goes out — it just
        // carries the count from 5 to 6 and stamps the timestamp to now, so the user is locked
        // out again immediately and, since the count only ever grows, for good. The allowance
        // has to actually start over.
        var harness = new ResetHarness();
        var token = TokenFor(harness.User, requestCount: 5, lastRequested: DateTime.UtcNow.AddDays(-2));
        harness.ResetTokens.Seed(token);

        await harness.Sut.ForgotPasswordAsync(Email, default);

        Assert.Single(harness.Bus.Published.OfType<SendResetCodeMessage>());
        Assert.Equal(1, token.RequestCount);
    }

    [Fact]
    public async Task A_day_later_the_user_gets_a_whole_new_allowance_rather_than_one_request()
    {
        // The consequence stated the other way round, because it is the one a user would feel:
        // five more attempts, not a single one before the door shuts again.
        var harness = new ResetHarness();
        harness.ResetTokens.Seed(
            TokenFor(harness.User, requestCount: 5, lastRequested: DateTime.UtcNow.AddDays(-2)));

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await harness.Sut.ForgotPasswordAsync(Email, default);
        }

        Assert.Equal(5, harness.Bus.Published.OfType<SendResetCodeMessage>().Count());
    }

    // --- redeeming it ------------------------------------------------------------------

    [Fact]
    public async Task A_valid_code_changes_the_password()
    {
        var harness = new ResetHarness();
        var before = harness.User.PasswordHash;
        harness.ResetTokens.Seed(TokenFor(harness.User));

        await harness.Sut.ResetPasswordAsync(Request("new-password"), default);

        Assert.NotEqual(before, harness.User.PasswordHash);
    }

    [Fact]
    public async Task A_code_works_once_and_then_never_again()
    {
        // The property the whole flow rests on. A replayable code means anyone who ever saw one
        // — in an email, a log, a screenshot, a shared inbox — owns the account permanently, and
        // the victim has no way to notice.
        var harness = new ResetHarness();
        harness.ResetTokens.Seed(TokenFor(harness.User));

        await harness.Sut.ResetPasswordAsync(Request("first"), default);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Sut.ResetPasswordAsync(Request("second"), default));
        Assert.Single(harness.ResetTokens.Deleted);
    }

    [Fact]
    public async Task An_expired_code_is_refused_and_left_in_place()
    {
        var harness = new ResetHarness();
        harness.ResetTokens.Seed(TokenFor(harness.User, expiresAt: DateTime.UtcNow.AddMinutes(-1)));
        var before = harness.User.PasswordHash;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Sut.ResetPasswordAsync(Request("new"), default));

        Assert.Equal(before, harness.User.PasswordHash);
        // Not deleted: a failed attempt must not burn a code the legitimate user is still about
        // to use, or an attacker could lock someone out simply by guessing wrong.
        Assert.Empty(harness.ResetTokens.Deleted);
    }

    [Fact]
    public async Task A_wrong_code_is_refused_and_leaves_the_password_alone()
    {
        var harness = new ResetHarness();
        harness.ResetTokens.Seed(TokenFor(harness.User));
        var before = harness.User.PasswordHash;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Sut.ResetPasswordAsync(Request("new", code: "not-the-code"), default));

        Assert.Equal(before, harness.User.PasswordHash);
        Assert.Empty(harness.ResetTokens.Deleted);
    }

    [Fact]
    public async Task A_reset_with_no_outstanding_code_is_refused()
    {
        var harness = new ResetHarness();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Sut.ResetPasswordAsync(Request("new"), default));
    }

    // --- the sessions that existed before -----------------------------------------------

    [Fact]
    public async Task Resetting_a_password_ends_every_session_opened_with_the_old_one()
    {
        // The defect. Resetting a password is what somebody does when they believe their account
        // is compromised — and it left every existing refresh token alive, so an attacker holding
        // a stolen one kept renewing it indefinitely AFTER the victim had "locked them out". The
        // victim's own recovery step was the thing that gave them false confidence.
        //
        // UserStatusService already revokes sessions on rejection and deactivation, so the rule
        // was known. This was the third door into the same room, and it was standing open.
        var harness = new ResetHarness();
        var stolen = ActiveToken(harness.User);
        var alsoStolen = ActiveToken(harness.User);
        harness.User.RefreshTokens = [stolen, alsoStolen];
        harness.ResetTokens.Seed(TokenFor(harness.User));

        await harness.Sut.ResetPasswordAsync(Request("new"), default);

        Assert.True(stolen.IsRevoked);
        Assert.True(alsoStolen.IsRevoked);
    }

    [Fact]
    public async Task The_sessions_are_read_through_the_query_that_actually_loads_them()
    {
        // The trap underneath the fix: `FindByEmail` does not Include the refresh tokens, so a
        // revocation loop over the user it returns iterates an empty collection and reports
        // success while revoking nothing. This asserts the second query was used at all.
        var harness = new ResetHarness();
        harness.User.RefreshTokens = [ActiveToken(harness.User)];
        harness.ResetTokens.Seed(TokenFor(harness.User));

        await harness.Sut.ResetPasswordAsync(Request("new"), default);

        Assert.Contains(harness.User.Id, harness.Users.LoadedWithRefreshTokens);
    }

    [Fact]
    public async Task A_failed_reset_leaves_the_sessions_alone()
    {
        // Revoking on a REFUSED attempt would hand an attacker a denial of service: guess wrong
        // at a stranger's reset code repeatedly and log them out of every device they own.
        var harness = new ResetHarness();
        var session = ActiveToken(harness.User);
        harness.User.RefreshTokens = [session];
        harness.ResetTokens.Seed(TokenFor(harness.User));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Sut.ResetPasswordAsync(Request("new", code: "not-the-code"), default));

        Assert.False(session.IsRevoked);
    }

    // --- helpers -------------------------------------------------------------------------

    private static ResetPasswordRequest Request(string newPassword, string code = Code)
        => new(Email, code, newPassword);

    private static RefreshToken ActiveToken(User user) => new()
    {
        Id = Guid.NewGuid(),
        Token = Guid.NewGuid().ToString(),
        ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
        IsRevoked = false,
        UserId = user.Id,
    };

    private static ResetPasswordToken TokenFor(
        User user,
        DateTime? expiresAt = null,
        int requestCount = 1,
        DateTime? lastRequested = null) => new()
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            User = user,
            Token = new FakeHasher().Hash(Code),
            ExpiresAtUtc = expiresAt ?? DateTime.UtcNow.AddMinutes(15),
            RequestCount = requestCount,
            LastRequestedAtUtc = lastRequested ?? DateTime.UtcNow,
        };

    /// <summary>Records what would have been written, so the assertion is about the log itself
    /// rather than about a developer having remembered not to write to it.</summary>
    private sealed class CapturingLogger : ILogger<AuthService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(
            LogLevel level, EventId id, TState state, Exception? error,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, error));
    }
}

/// <summary>
/// A seeded, active user plus the pieces the reset flow touches. Separate from
/// <c>AuthServiceCoreTests.Harness</c> only because the logger has to be swappable here — the
/// point of one of these cases is what does NOT reach it.
/// </summary>
internal sealed class ResetHarness
{
    public readonly SeedableUserRepository Users = new();
    public readonly RecordingResetTokenRepository ResetTokens = new();
    public readonly RecordingEventBus Bus = new();
    public readonly User User;
    public readonly AuthService Sut;

    public ResetHarness(ILogger<AuthService>? logger = null)
    {
        var studentRole = Role.Create(RoleName.Student);
        User = new User
        {
            Id = Guid.NewGuid(),
            Email = "amina@intellilect.io",
            UserName = "amina",
            FirstName = "Amina",
            LastName = "Rahman",
            PasswordHash = new FakeHasher().Hash("the-old-password"),
            RoleId = studentRole.Id,
            Role = studentRole,
            RefreshTokens = [],
        };
        // Status has a private setter and its own transitions, so it is reached the way the
        // application reaches it rather than by reflection.
        User.Approve();
        Users.Seed(User);

        Sut = new AuthService(
            Users,
            new SeedableRoleRepository(studentRole),
            new NotUsedRoleRepository(),
            new FakeHasher(),
            new RecordingJwtProvider(),
            new RecordingRefreshTokenRepository(),
            ResetTokens,
            new FixedResetTokenGenerator(),
            new FakeTwoFactorChallengeRepository(),
            new StubTwoFactorCodeGenerator("123456"),
            TestMapper.Create(),
            Bus,
            logger ?? NullLogger<AuthService>.Instance);
    }
}
