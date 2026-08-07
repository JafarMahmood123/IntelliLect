using AutoMapper;
using IntelliLect.Contracts.Messages;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Authentication;
using UserManagementService.Application.DTOs;
using UserManagementService.Application.DTOs.Auth;
using UserManagementService.Domain.Entities;
using UserManagementService.Domain.Policies;

using Microsoft.Extensions.Logging.Abstractions;

namespace UserManagementService.UnitTests.Authentication;

/// <summary>
/// Registration, login and session renewal — test-plan area A.
///
/// The security core, and until now the least-covered part of the system: only logout and the
/// two-factor path had tests. What these protect is the set of rules that decide who gets a
/// session and who keeps one, because every other authorization check in the stack assumes this
/// one already happened.
/// </summary>
public sealed class AuthServiceCoreTests
{
    private const string Password = "correct-horse";
    private const string Email = "amina@intellilect.io";

    // --- registration -----------------------------------------------------------------

    [Fact]
    public async Task Registration_creates_a_pending_account_never_an_active_one()
    {
        // A-01. The whole approval workflow rests on this default: a self-registered account that
        // arrived Active would skip every admin decision in the product.
        var harness = new Harness();

        var id = await harness.Sut.RegisterAsync(RegisterRequest(harness.StudentRole.Id));

        var created = Assert.Single(harness.Users.Added);
        Assert.Equal(id, created.Id);
        Assert.Equal(UserStatus.Pending, created.Status);
    }

    [Fact]
    public async Task Registration_stores_a_hash_and_never_the_password()
    {
        // A-06.
        var harness = new Harness();

        await harness.Sut.RegisterAsync(RegisterRequest(harness.StudentRole.Id));

        var created = Assert.Single(harness.Users.Added);
        Assert.NotEqual(Password, created.PasswordHash);
        Assert.Equal(FakeHasher.Hashed(Password), created.PasswordHash);
    }

    [Fact]
    public async Task Registration_refuses_a_privileged_role()
    {
        // Self-registration must not be a route to Admin. The role list the UI offers is a
        // convenience; this is the rule.
        var harness = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Sut.RegisterAsync(RegisterRequest(harness.AdminRole.Id)));
        Assert.Empty(harness.Users.Added);
    }

    [Fact]
    public async Task Registration_refuses_an_unknown_role()
    {
        var harness = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Sut.RegisterAsync(RegisterRequest(Guid.NewGuid())));
    }

    [Fact]
    public async Task Registration_refuses_an_email_that_already_exists()
    {
        var harness = new Harness();
        harness.Users.Seed(ActiveUser(harness.StudentRole));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Sut.RegisterAsync(RegisterRequest(harness.StudentRole.Id)));
        Assert.Empty(harness.Users.Added);
    }

    [Fact]
    public async Task Registration_refuses_an_email_that_differs_only_in_case()
    {
        // The user-visible half of the defect, and it needs no concurrency at all. The seeded
        // account is `amina@intellilect.io`; this asks to register `AMINA@IntelliLect.IO`.
        //
        // Before the fix that was a second account: the real query compared exactly, so the
        // existence check found nothing. The owner could then sign in only when their
        // capitalisation matched the row the query happened to return, a reset for the other
        // spelling silently found nobody, and an administrator approved one of the two.
        var harness = new Harness();
        harness.Users.Seed(ActiveUser(harness.StudentRole));

        var shouted = new RegisterRequest(
            "amina", Email.ToUpperInvariant(), "Amina", "Test", harness.StudentRole.Id, Password);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Sut.RegisterAsync(shouted));
        Assert.Empty(harness.Users.Added);
    }

    [Fact]
    public async Task A_registered_address_is_stored_in_its_canonical_form()
    {
        // The other half. The constraint that now backs this check can only enforce identity on
        // the value actually stored, so a mixed-case row would slip past a unique index and
        // become unreachable to every lookup, all of which normalise what they are given.
        var harness = new Harness();

        await harness.Sut.RegisterAsync(new RegisterRequest(
            "amina", "  AMINA@IntelliLect.IO ", "Amina", "Test", harness.StudentRole.Id, Password));

        var added = Assert.Single(harness.Users.Added);
        Assert.Equal("amina@intellilect.io", added.Email);
    }

    [Fact]
    public async Task Logging_in_does_not_depend_on_how_the_address_was_typed()
    {
        // What the person actually experiences. Typing the address with different capitalisation
        // than at registration used to be "invalid credentials" for an account that exists — the
        // one failure a user has no way to diagnose or work around.
        var harness = new Harness();
        harness.Users.Seed(ActiveUser(harness.StudentRole));

        var result = await harness.Sut.LoginAsync(
            new LoginRequest(Email.ToUpperInvariant(), Password), default);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task Registration_announces_the_pending_account_before_committing()
    {
        // The outbox row and the account row have to land in the same SaveChanges. Publishing
        // after the commit is how the "your registration was received" email goes missing.
        var harness = new Harness();

        await harness.Sut.RegisterAsync(RegisterRequest(harness.StudentRole.Id));

        var published = Assert.Single(harness.Bus.Published.OfType<UserStatusChangedMessage>());
        Assert.Equal(UserStatus.Pending.ToString(), published.Status);
        // The outbox row must already exist when the account row commits.
        Assert.Equal(1, harness.Users.PublishedCountAtSave);
        Assert.Equal(1, harness.Users.SaveCount);
    }

    // --- login ------------------------------------------------------------------------

    [Fact]
    public async Task An_active_account_with_the_right_password_gets_a_session()
    {
        // A-04.
        var harness = new Harness();
        var user = ActiveUser(harness.StudentRole);
        harness.Users.Seed(user);

        var result = await harness.Sut.LoginAsync(new LoginRequest(Email, Password));

        Assert.NotNull(result.Tokens);
        Assert.Equal(user.Id, harness.Jwt.LastUserId);
        Assert.Equal(RoleName.Student.ToString(), harness.Jwt.LastRoleName);
        // A non-super-admin has not completed a second factor, and the token must not claim it.
        Assert.False(harness.Jwt.LastTwoFactorCompleted);
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_email_fail_identically()
    {
        // A-05. Two different failures with one message: anything that distinguishes them turns
        // the login form into a list of who has an account here.
        var harness = new Harness();
        harness.Users.Seed(ActiveUser(harness.StudentRole));

        var wrongPassword = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Sut.LoginAsync(new LoginRequest(Email, "wrong")));
        var unknownEmail = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Sut.LoginAsync(new LoginRequest("nobody@intellilect.io", Password)));

        Assert.Equal(wrongPassword.Message, unknownEmail.Message);
    }

    [Theory]
    [InlineData(UserStatus.Pending)]
    [InlineData(UserStatus.Rejected)]
    [InlineData(UserStatus.Deactivated)]
    public async Task A_non_active_account_cannot_sign_in(UserStatus status)
    {
        // A-02, A-03.
        var harness = new Harness();
        harness.Users.Seed(UserWithStatus(harness.StudentRole, status));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Sut.LoginAsync(new LoginRequest(Email, Password)));
        Assert.Null(harness.Jwt.LastUserId);
    }

    [Fact]
    public async Task Status_is_only_revealed_to_someone_who_already_knows_the_password()
    {
        // The status messages ("pending approval", "rejected") are deliberately specific — a user
        // locked out deserves to know why. That is only safe because they sit BEHIND the
        // credential check: without the password every outcome is the same generic failure, so
        // this is not an enumeration oracle. This test pins that ordering.
        var harness = new Harness();
        harness.Users.Seed(UserWithStatus(harness.StudentRole, UserStatus.Pending));

        var withPassword = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Sut.LoginAsync(new LoginRequest(Email, Password)));
        var withoutPassword = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Sut.LoginAsync(new LoginRequest(Email, "wrong")));

        Assert.Contains("pending", withPassword.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pending", withoutPassword.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_super_admin_gets_a_challenge_instead_of_a_session()
    {
        // A-13. Correct credentials are not enough, and nothing token-shaped may come back — a
        // second factor that returns a usable session first is not a second factor.
        var harness = new Harness();
        harness.Users.Seed(ActiveUser(harness.SuperAdminRole));

        var result = await harness.Sut.LoginAsync(new LoginRequest(Email, Password));

        Assert.True(result.RequiresTwoFactor);
        Assert.Null(result.Tokens);
        Assert.Null(harness.Jwt.LastUserId);
        Assert.NotNull(harness.TwoFactor.Added);
    }

    // --- refresh ----------------------------------------------------------------------

    [Fact]
    public async Task Refreshing_rotates_the_token_and_revokes_the_old_one()
    {
        // A-11. Rotation is only worth anything if the old token dies with the swap.
        var harness = new Harness();
        var token = ActiveToken();
        var user = ActiveUser(harness.StudentRole, token);
        harness.Users.Seed(user);

        var response = await harness.Sut.RefreshAsync(new RefreshTokenRequest(token.Token), default);

        Assert.True(token.IsRevoked);
        Assert.NotEqual(token.Token, response.RefreshToken);
        Assert.NotNull(harness.RefreshTokens.Added);
    }

    [Fact]
    public async Task A_replayed_refresh_token_is_refused()
    {
        // The second half of rotation: presenting the token that was just spent must fail, which
        // is what makes a stolen token useful only until its owner next refreshes.
        var harness = new Harness();
        var token = ActiveToken();
        harness.Users.Seed(ActiveUser(harness.StudentRole, token));

        await harness.Sut.RefreshAsync(new RefreshTokenRequest(token.Token), default);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Sut.RefreshAsync(new RefreshTokenRequest(token.Token), default));
    }

    [Fact]
    public async Task An_expired_refresh_token_is_refused()
    {
        var harness = new Harness();
        var token = ActiveToken();
        token.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        harness.Users.Seed(ActiveUser(harness.StudentRole, token));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Sut.RefreshAsync(new RefreshTokenRequest(token.Token), default));
    }

    [Theory]
    [InlineData(UserStatus.Rejected)]
    [InlineData(UserStatus.Deactivated)]
    public async Task A_deactivated_account_cannot_renew_its_session(UserStatus status)
    {
        // Rejection and deactivation already revoke every active token, so in practice this token
        // would be dead. This is the second lock: renewing a session is the one operation that can
        // outlive the decision to end it, and it must not depend on another method having
        // remembered to revoke.
        var harness = new Harness();
        var token = ActiveToken();
        harness.Users.Seed(UserWithStatus(harness.StudentRole, status, token));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Sut.RefreshAsync(new RefreshTokenRequest(token.Token), default));
        Assert.Null(harness.RefreshTokens.Added);
    }

    [Fact]
    public async Task A_super_admins_rotated_token_keeps_its_two_factor_marking()
    {
        // Their refresh token only ever exists after a verified second factor, so dropping the
        // marking on rotation would lock them out of every SuperAdminTwoFactor route an hour
        // into their session.
        var harness = new Harness();
        var token = ActiveToken();
        harness.Users.Seed(ActiveUser(harness.SuperAdminRole, token));

        await harness.Sut.RefreshAsync(new RefreshTokenRequest(token.Token), default);

        Assert.True(harness.Jwt.LastTwoFactorCompleted);
    }

    // --- password reset ---------------------------------------------------------------

    [Fact]
    public async Task Forgot_password_for_an_unknown_email_does_nothing_and_says_nothing()
    {
        // A-09. Returning quietly is the point: a different response for a registered address
        // would make this endpoint an account-existence oracle that needs no password at all.
        var harness = new Harness();

        await harness.Sut.ForgotPasswordAsync("nobody@intellilect.io", default);

        Assert.Empty(harness.Bus.Published);
    }

    [Fact]
    public async Task Forgot_password_for_a_known_email_issues_a_code()
    {
        var harness = new Harness();
        harness.Users.Seed(ActiveUser(harness.StudentRole));

        await harness.Sut.ForgotPasswordAsync(Email, default);

        Assert.Single(harness.Bus.Published.OfType<SendResetCodeMessage>());
    }

    // --- helpers ----------------------------------------------------------------------

    private static RegisterRequest RegisterRequest(Guid roleId)
        => new("amina", Email, "Amina", "Test", roleId, Password);

    private static RefreshToken ActiveToken() => new()
    {
        Id = Guid.NewGuid(),
        Token = "refresh-original",
        IsRevoked = false,
        ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
    };

    private static User ActiveUser(Role role, RefreshToken? token = null)
        => UserWithStatus(role, UserStatus.Active, token);

    private static User UserWithStatus(Role role, UserStatus status, RefreshToken? token = null)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = "amina",
            Email = Email,
            FirstName = "Amina",
            LastName = "Test",
            PasswordHash = FakeHasher.Hashed(Password),
            RoleId = role.Id,
            Role = role,
        };

        // Status has a private setter and its own transitions, so it is reached the same way the
        // application reaches it rather than by reflection.
        switch (status)
        {
            case UserStatus.Active: user.Approve(); break;
            case UserStatus.Rejected: user.Reject(); break;
            case UserStatus.Deactivated: user.Approve(); user.Deactivate(); break;
            case UserStatus.Pending: break;
        }

        if (token is not null)
        {
            token.UserId = user.Id;
            user.RefreshTokens.Add(token);
        }

        return user;
    }

    /// <summary>Everything the service needs, with only the collaborators these cases exercise.</summary>
    private sealed class Harness
    {
        public readonly Role StudentRole = Role.Create(RoleName.Student);
        public readonly Role AdminRole = Role.Create(RoleName.Admin);
        public readonly Role SuperAdminRole = Role.Create(RoleName.SuperAdmin);

        public readonly SeedableUserRepository Users = new();
        public readonly RecordingRefreshTokenRepository RefreshTokens = new();
        public readonly RecordingEventBus Bus = new();
        public readonly RecordingJwtProvider Jwt = new();
        public readonly FakeTwoFactorChallengeRepository TwoFactor = new();
        public readonly RecordingResetTokenRepository ResetTokens = new();
        public readonly AuthService Sut;

        public Harness()
        {
            Users.PublishedSoFar = () => Bus.Published.Count;
            var roles = new SeedableRoleRepository(StudentRole, AdminRole, SuperAdminRole);

            Sut = new AuthService(
                Users,
                roles,
                new NotUsedRoleRepository(),
                new FakeHasher(),
                Jwt,
                RefreshTokens,
                ResetTokens,
                new FixedResetTokenGenerator(),
                TwoFactor,
                new StubTwoFactorCodeGenerator("123456"),
                TestMapper.Create(),
                Bus,
                NullLogger<AuthService>.Instance);
        }
    }
}


// --- fakes used only by these cases; the shared ones live in AuthServiceTwoFactorTests ---

/// <summary>A user table: seeded rows are found, new rows are recorded.</summary>
internal sealed class SeedableUserRepository : IUserRepository
{
    private readonly List<User> _users = [];

    public List<User> Added { get; } = [];
    public int SaveCount { get; private set; }
    /// <summary>How many messages the bus had seen when the first save happened.</summary>
    public int PublishedCountAtSave { get; private set; } = -1;
    public Func<int> PublishedSoFar { get; set; } = () => 0;

    public void Seed(User user) => _users.Add(user);

    /// <summary>
    /// Mirrors the real repository: normalise the argument, then compare EXACTLY — because the
    /// stored value is canonical and Postgres compares strings exactly.
    ///
    /// This used `StringComparison.OrdinalIgnoreCase`, which is how everyone assumed the system
    /// behaved. The real query was `u.Email == email`, case-sensitive, so `Jafar@x.com` and
    /// `jafar@x.com` were two accounts in production and one account in every test.
    /// `Registration_refuses_an_email_that_already_exists` passed the whole time.
    ///
    /// The same lesson as `FakeStreamRepository` one cycle earlier, from the other direction: a
    /// double that is more FORGIVING than the database hides a defect exactly as effectively as
    /// one that is more permissive.
    /// </summary>
    public Task<User?> FindByEmail(string email, CancellationToken ct = default)
    {
        var canonical = EmailIdentity.Normalize(email);
        return Task.FromResult(_users.FirstOrDefault(u => u.Email == canonical));
    }

    public Task<User?> FindByRefreshToken(string token, CancellationToken ct)
        => Task.FromResult(_users.FirstOrDefault(u => u.RefreshTokens.Any(t => t.Token == token)));

    public Task AddAsync(User entity, CancellationToken ct = default)
    {
        Added.Add(entity);
        _users.Add(entity);
        return Task.CompletedTask;
    }

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        if (SaveCount == 0) PublishedCountAtSave = PublishedSoFar();
        SaveCount++;
        return Task.FromResult(1);
    }

    /// <summary>The seeded row WITH its sessions. FindByEmail deliberately does not include
    /// them, which is the trap ResetPasswordAsync has to avoid.</summary>
    public Task<User?> GetByIdWithRefreshTokensAsync(Guid id, CancellationToken ct = default)
    {
        LoadedWithRefreshTokens.Add(id);
        return Task.FromResult(_users.FirstOrDefault(u => u.Id == id));
    }

    /// <summary>Which ids were loaded through the query that actually Includes the sessions.</summary>
    public List<Guid> LoadedWithRefreshTokens { get; } = [];
    public Task<List<User>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<User>> GetByIdsWithRefreshTokensAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<User?> FindByResetToken(string token, CancellationToken ct) => throw new NotImplementedException();
    public Task<(List<User> Items, int TotalCount)> GetPaginatedPendingUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct) => throw new NotImplementedException();
    public Task<(List<User> Items, int TotalCount)> GetPaginatedUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct) => throw new NotImplementedException();
    public Task<(List<User> Items, int TotalCount)> SearchUsersAsync(UserManagementService.Application.Common.Users.UserQuerySpecification specification, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
    /// <summary>The rows are the same objects the service mutated, so an update is a no-op —
    /// but it must not throw, or a service that legitimately saves a change cannot be tested.</summary>
    public Task UpdateAsync(User entity, CancellationToken ct = default)
    {
        Updated.Add(entity);
        return Task.CompletedTask;
    }

    public List<User> Updated { get; } = [];
    public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
}

internal sealed class SeedableRoleRepository : IRepository<Role>
{
    private readonly Role[] _roles;
    public SeedableRoleRepository(params Role[] roles) => _roles = roles;

    public Task<Role?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(_roles.FirstOrDefault(r => r.Id == id));

    public Task AddAsync(Role entity, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpdateAsync(Role entity, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<int> SaveChangesAsync(CancellationToken ct = default) => throw new NotImplementedException();
}

internal sealed class RecordingResetTokenRepository : IResetTokenRepository
{
    private readonly Dictionary<Guid, ResetPasswordToken> _byUser = new();

    public ResetPasswordToken? Added { get; private set; }
    public List<Guid> Deleted { get; } = [];
    public int SaveCount { get; private set; }
    public int UpdateCount { get; private set; }

    public void Seed(ResetPasswordToken token) => _byUser[token.UserId] = token;

    public Task<ResetPasswordToken?> FindResetPasswordTokenByUserId(Guid userId)
        => Task.FromResult(_byUser.GetValueOrDefault(userId));

    public Task AddAsync(ResetPasswordToken entity, CancellationToken ct = default)
    {
        Added = entity;
        _byUser[entity.UserId] = entity;
        return Task.CompletedTask;
    }

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        SaveCount++;
        return Task.FromResult(1);
    }

    public Task UpdateAsync(ResetPasswordToken entity, CancellationToken ct = default)
    {
        UpdateCount++;
        return Task.CompletedTask;
    }

    public Task<ResetPasswordToken?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        Deleted.Add(id);
        foreach (var (userId, token) in _byUser.Where(pair => pair.Value.Id == id).ToList())
        {
            _byUser.Remove(userId);
        }
        return Task.CompletedTask;
    }
}

internal sealed class FixedResetTokenGenerator : IResetPasswordTokenGenerator
{
    public string Generate() => "reset-code-000000";
}
