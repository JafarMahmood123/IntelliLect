using IntelliLect.Contracts.Messages;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Authentication;
using UserManagementService.Application.DTOs;
using UserManagementService.Domain.Entities;
using UserManagementService.Domain.Policies;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace UserManagementService.UnitTests.Authentication;

/// <summary>
/// Sign-in lockout — test-plan A-07.
///
/// This one was not a missing test. It was a missing feature: before this file there was no
/// limit of any kind on password guessing. The reset endpoint caps a user at five codes a day
/// and the two-factor challenge dies after a fixed number of wrong codes, so the *front* door —
/// the one that takes a password and hands back a session — was the only one in the building
/// that would answer an unlimited number of guesses. Nothing above it helped: there is no rate
/// limiter in the API and no <c>limit_req</c> in the nginx config.
///
/// Two properties do all the work here, and they pull in opposite directions:
///
///   * **The lock must not tell anyone anything.** It is checked BEFORE the password, and it
///     answers with the same generic failure a wrong password and an unknown email get. Checked
///     afterwards, or answered specifically, it would leak the very thing it is defending.
///   * **The lock must lift by itself, and must not be extendable by the attacker.** A defence
///     that a stranger can hold down indefinitely against one named account is not a defence.
/// </summary>
public sealed class AuthServiceLockoutTests
{
    private const string Password = "correct-horse";
    private const string Email = "amina@intellilect.io";

    // --- the policy, on its own --------------------------------------------------------

    [Fact]
    public void An_account_that_has_never_failed_is_not_locked()
    {
        Assert.False(LoginLockout.IsLockedOut(null, DateTime.UtcNow));
    }

    [Theory]
    [InlineData(-1, true)]   // a second before the lock ends
    [InlineData(0, false)]   // the instant it ends
    [InlineData(1, false)]   // a second after
    public void The_lock_ends_at_the_instant_it_says_it_does(int offsetSeconds, bool expectedLocked)
    {
        // The boundary is written in exactly one place, which is the reason LoginLockout exists
        // as a type at all. QuizDeadline earned the same treatment for the same reason: a rule
        // spelled out twice is a rule that will eventually disagree with itself, and this one is
        // asked from both directions — "refuse this attempt?" and "has it lifted?".
        var endsAt = new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(expectedLocked, LoginLockout.IsLockedOut(endsAt, endsAt.AddSeconds(offsetSeconds)));
    }

    [Fact]
    public void Failures_short_of_the_limit_do_not_lock()
    {
        var now = DateTime.UtcNow;
        var user = new User();

        for (var i = 0; i < LoginLockout.MaxFailedAttempts - 1; i++)
        {
            user.RegisterFailedLogin(now);
        }

        Assert.Equal(LoginLockout.MaxFailedAttempts - 1, user.FailedLoginCount);
        Assert.Null(user.LockoutEndsAtUtc);
        Assert.False(user.IsLockedOut(now));
    }

    [Fact]
    public void The_failure_that_reaches_the_limit_locks_for_the_configured_time()
    {
        var now = new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);
        var user = new User();

        for (var i = 0; i < LoginLockout.MaxFailedAttempts; i++)
        {
            user.RegisterFailedLogin(now);
        }

        Assert.Equal(now.Add(LoginLockout.Duration), user.LockoutEndsAtUtc);
        Assert.True(user.IsLockedOut(now));
    }

    [Fact]
    public void Guessing_during_a_lock_does_not_extend_it()
    {
        // The anti-denial-of-service rule. If every attempt pushed the end further out, anyone
        // who knew an address could keep its owner permanently locked out with a loop — an
        // attack that always succeeds, in place of one that mostly does not. So the lock is a
        // fixed window from the attempt that tripped it, not a sliding one.
        var now = new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);
        var user = Locked(now);
        var lockedUntil = user.LockoutEndsAtUtc;

        for (var i = 0; i < 50; i++)
        {
            user.RegisterFailedLogin(now.AddMinutes(1));
        }

        Assert.Equal(lockedUntil, user.LockoutEndsAtUtc);
        Assert.Equal(LoginLockout.MaxFailedAttempts, user.FailedLoginCount);
    }

    [Fact]
    public void A_lock_that_has_run_out_gives_back_the_whole_allowance()
    {
        // Not "one more attempt". The count belongs to the run of failures that caused the lock,
        // and that run is over — so the next mistake starts at one and the user has the full
        // five again. Asserting only that the sixth attempt is accepted would pass against an
        // implementation that locks again on the very next wrong password, which is a lockout
        // that becomes permanent after the first one.
        var lockedAt = DateTime.UtcNow.AddHours(-1);
        var user = Locked(lockedAt);
        var afterwards = lockedAt.Add(LoginLockout.Duration);

        for (var i = 1; i < LoginLockout.MaxFailedAttempts; i++)
        {
            user.RegisterFailedLogin(afterwards);
            Assert.Equal(i, user.FailedLoginCount);
            Assert.Null(user.LockoutEndsAtUtc);
        }

        user.RegisterFailedLogin(afterwards);
        Assert.NotNull(user.LockoutEndsAtUtc);
    }

    [Fact]
    public void A_correct_password_ends_the_run()
    {
        var now = DateTime.UtcNow;
        var user = new User();
        user.RegisterFailedLogin(now);
        user.RegisterFailedLogin(now);

        user.RegisterSuccessfulLogin();

        Assert.Equal(0, user.FailedLoginCount);
        Assert.Null(user.LockoutEndsAtUtc);
        Assert.False(user.HasFailedLoginHistory);
    }

    [Fact]
    public void A_failed_sign_in_does_not_roll_the_concurrency_token()
    {
        // Version guards edits to the account against a concurrent writer. A failed sign-in is
        // not an edit anybody is racing, and rolling it would make two people mistyping their
        // password at the same moment into an optimistic-concurrency conflict — one of them
        // losing an unrelated profile save to a stranger's typo.
        var user = new User();
        var before = user.Version;

        user.RegisterFailedLogin(DateTime.UtcNow);
        user.RegisterSuccessfulLogin();

        Assert.Equal(before, user.Version);
    }

    // --- the policy, wired into login --------------------------------------------------

    [Fact]
    public async Task Five_wrong_passwords_shut_the_door_on_the_right_one()
    {
        // The whole point: after the run, the correct password stops working too. A limit that
        // only refused further *wrong* passwords would refuse nothing at all.
        var harness = new LockoutHarness();

        for (var i = 0; i < LoginLockout.MaxFailedAttempts; i++)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => harness.Sut.LoginAsync(new LoginRequest(Email, "wrong")));
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Sut.LoginAsync(new LoginRequest(Email, Password)));
        Assert.Null(harness.Jwt.LastUserId);
    }

    [Fact]
    public async Task A_locked_account_fails_exactly_like_a_wrong_password_and_an_unknown_email()
    {
        // A-05 extended to the lock. Three different situations, one message: the attacker who
        // caused the lock must not be told they caused it, and the address must not be confirmed
        // as registered by the fact that its answer changed.
        var harness = new LockoutHarness();
        harness.LockTheAccount();

        var locked = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Sut.LoginAsync(new LoginRequest(Email, Password)));
        var unknownEmail = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Sut.LoginAsync(new LoginRequest("nobody@intellilect.io", Password)));

        Assert.Equal(unknownEmail.Message, locked.Message);
        Assert.DoesNotContain("lock", locked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("attempt", locked.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_locked_account_is_refused_without_the_password_being_looked_at()
    {
        // This is the ordering that keeps the case above honest. If the lock were enforced after
        // the credential check, the reply could differ between a wrong password and the right
        // one — and an attacker guessing through a lockout would learn the password from the
        // difference, having never been let in. Checking first makes that impossible rather than
        // merely unlikely, so the check is on the hasher, not on the message.
        var harness = new LockoutHarness();
        harness.LockTheAccount();
        var before = harness.Hasher.VerifyCount;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Sut.LoginAsync(new LoginRequest(Email, Password)));

        Assert.Equal(before, harness.Hasher.VerifyCount);
    }

    [Fact]
    public async Task The_owner_gets_back_in_once_the_lock_expires()
    {
        // "Lockout expires" is the second half of A-07, and the half that decides whether this
        // feature is usable at all: nobody is watching an unlock queue on this system, so a lock
        // that needed an administrator would be permanent in practice.
        var harness = new LockoutHarness();
        harness.LockTheAccount(at: DateTime.UtcNow.Subtract(LoginLockout.Duration).AddMinutes(-1));

        var result = await harness.Sut.LoginAsync(new LoginRequest(Email, Password));

        Assert.NotNull(result.Tokens);
    }

    [Fact]
    public async Task Signing_in_successfully_clears_the_run()
    {
        // Four near-misses, one success, four more near-misses: still not locked. Without the
        // reset the count only grows, so an account that fumbled four times months ago spends
        // the rest of its life one typo from a lockout — and one typo from the next.
        var harness = new LockoutHarness();
        await FailTimes(harness, LoginLockout.MaxFailedAttempts - 1);

        await harness.Sut.LoginAsync(new LoginRequest(Email, Password));
        await FailTimes(harness, LoginLockout.MaxFailedAttempts - 1);

        var result = await harness.Sut.LoginAsync(new LoginRequest(Email, Password));
        Assert.NotNull(result.Tokens);
    }

    [Fact]
    public async Task Every_failed_attempt_is_written_down()
    {
        // The counter lives on a row, and a request that increments it in memory and returns
        // leaves nothing behind — five separate HTTP requests would each be the first failure.
        // Nothing else in this file would notice, because the fakes hand back the same instance.
        var harness = new LockoutHarness();

        await FailTimes(harness, 3);

        Assert.Equal(3, harness.Users.SaveCount);
        Assert.Equal(3, harness.User.FailedLoginCount);
    }

    [Fact]
    public async Task An_unknown_email_leaves_nothing_behind()
    {
        // There is no row to count against, and inventing one would build exactly the list of
        // attempted addresses that the identical error messages exist to keep from being built.
        var harness = new LockoutHarness();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Sut.LoginAsync(new LoginRequest("nobody@intellilect.io", Password)));

        Assert.Equal(0, harness.Users.SaveCount);
    }

    [Fact]
    public async Task An_ordinary_sign_in_does_not_write_to_the_user_row()
    {
        // The reset is guarded on there being something to reset. Unguarded, every successful
        // login in the system becomes a write to Users — and, because Version is a concurrency
        // token, a write that can lose to a concurrent profile edit and fail the login.
        var harness = new LockoutHarness();

        await harness.Sut.LoginAsync(new LoginRequest(Email, Password));

        Assert.Equal(0, harness.Users.SaveCount);
    }

    [Fact]
    public async Task Tripping_the_lock_is_logged_once_and_never_with_the_password()
    {
        // Operators need to see a brute-force attempt; that is the whole operational value of
        // the feature. What they must not see is what was tried — a near-miss in a log file is
        // still somebody's password, and A-24 already had to remove one secret from this sink.
        var logger = new RecordingLogger();
        var harness = new LockoutHarness(logger: logger);

        await FailTimes(harness, LoginLockout.MaxFailedAttempts + 3);

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        Assert.Contains(harness.User.Id.ToString(), warnings[0].Message);
        Assert.DoesNotContain("wrong", string.Join(" ", logger.Entries.Select(e => e.Message)));
    }

    [Fact]
    public async Task A_locked_super_admin_is_sent_no_verification_code()
    {
        // The lock sits ahead of the two-factor challenge, not beside it. Behind it, guessing at
        // a super admin's password would mail them a fresh code every attempt — turning a failed
        // brute force into a mail flood at the one address that matters most, and training its
        // owner to ignore the message that means someone is trying to get in.
        var harness = new LockoutHarness(role: RoleName.SuperAdmin);
        harness.LockTheAccount();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Sut.LoginAsync(new LoginRequest(Email, Password)));

        Assert.Null(harness.TwoFactor.Added);
        Assert.Empty(harness.Bus.Published.OfType<SendTwoFactorCodeMessage>());
    }

    [Fact]
    public async Task An_account_awaiting_approval_locks_too()
    {
        // The counter sits in front of the status switch, so it protects accounts that cannot
        // sign in yet. They are the better target, not the worse one: a pending account's owner
        // is not expecting it to work, so nothing about a run of guesses looks wrong to them.
        var harness = new LockoutHarness(approved: false);

        await FailTimes(harness, LoginLockout.MaxFailedAttempts);

        Assert.True(harness.User.IsLockedOut(DateTime.UtcNow));
    }

    // --- helpers ------------------------------------------------------------------------

    private static async Task FailTimes(LockoutHarness harness, int times)
    {
        for (var i = 0; i < times; i++)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => harness.Sut.LoginAsync(new LoginRequest(Email, "wrong")));
        }
    }

    /// <summary>An account taken to the limit at <paramref name="at"/>, the way login takes it.</summary>
    private static User Locked(DateTime at)
    {
        var user = new User();
        for (var i = 0; i < LoginLockout.MaxFailedAttempts; i++)
        {
            user.RegisterFailedLogin(at);
        }
        return user;
    }

    /// <summary>
    /// One seeded account plus the collaborators login touches. Separate from
    /// <c>AuthServiceCoreTests.Harness</c> because these cases need the role, the approval and
    /// the logger chosen per test, and a hasher that counts.
    /// </summary>
    private sealed class LockoutHarness
    {
        public readonly SeedableUserRepository Users = new();
        public readonly RecordingJwtProvider Jwt = new();
        public readonly RecordingEventBus Bus = new();
        public readonly FakeTwoFactorChallengeRepository TwoFactor = new();
        public readonly CountingHasher Hasher = new();
        public readonly User User;
        public readonly AuthService Sut;

        public LockoutHarness(
            RoleName role = RoleName.Student,
            bool approved = true,
            ILogger<AuthService>? logger = null)
        {
            var accountRole = Role.Create(role);
            User = new User
            {
                Id = Guid.NewGuid(),
                UserName = "amina",
                Email = Email,
                FirstName = "Amina",
                LastName = "Rahman",
                PasswordHash = FakeHasher.Hashed(Password),
                RoleId = accountRole.Id,
                Role = accountRole,
            };
            if (approved) User.Approve();
            Users.Seed(User);

            Sut = new AuthService(
                Users,
                new SeedableRoleRepository(accountRole),
                new NotUsedRoleRepository(),
                Hasher,
                Jwt,
                new RecordingRefreshTokenRepository(),
                new RecordingResetTokenRepository(),
                new FixedResetTokenGenerator(),
                TwoFactor,
                new StubTwoFactorCodeGenerator("123456"),
                TestMapper.Create(),
                Bus,
                logger ?? NullLogger<AuthService>.Instance);
        }

        /// <summary>Take the account to the limit, optionally at a moment already past.</summary>
        public void LockTheAccount(DateTime? at = null)
        {
            var when = at ?? DateTime.UtcNow;
            for (var i = 0; i < LoginLockout.MaxFailedAttempts; i++)
            {
                User.RegisterFailedLogin(when);
            }
        }
    }
}

/// <summary>A hasher that also reports whether anyone asked it to check a password.</summary>
internal sealed class CountingHasher : IHasher
{
    public int VerifyCount { get; private set; }

    public string Hash(string code) => FakeHasher.Hashed(code);

    public bool Verify(string oldCode, string codeHash)
    {
        VerifyCount++;
        return codeHash == FakeHasher.Hashed(oldCode);
    }
}

/// <summary>Keeps the level as well as the text; one of these cases is about a level.</summary>
internal sealed class RecordingLogger : ILogger<AuthService>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;

    public void Log<TState>(
        LogLevel level, EventId id, TState state, Exception? error,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((level, formatter(state, error)));
}
