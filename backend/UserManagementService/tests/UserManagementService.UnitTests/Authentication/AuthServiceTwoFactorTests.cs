using AutoMapper;
using IntelliLect.Contracts.Messages;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Authentication;
using UserManagementService.Application.DTOs;
using UserManagementService.Application.DTOs.Auth;
using UserManagementService.Domain.Entities;

namespace UserManagementService.UnitTests.Authentication;

// Unit tests for the super admin two-factor login use-case ("تسجيل دخول المدير الأعلى بالمصادقة الثنائية").
//
//   LoginAsync   (stage 1) -> credential + status checks, then either issue a challenge
//                             (super admin) or issue tokens immediately (everyone else).
//   VerifyTwoFactorAsync (stage 2) -> validate the emailed code and issue the 2FA-marked session.
//
// Every main-scenario step and alternate path (2أ, 2ب, 8أ, 8ب, 8ج) is covered.
public class AuthServiceTwoFactorTests
{
    private const string CorrectPassword = "Sup3rSecret!";
    private const string GeneratedCode = "246813";

    // ----- Stage 1: LoginAsync -------------------------------------------------

    [Fact]
    public async Task LoginAsync_SuperAdminWithValidCredentials_CreatesChallengeSendsCodeAndReturnsTwoFactorRequired()
    {
        // Arrange: an active super admin with correct credentials (main steps 1-3).
        var user = ActiveSuperAdmin();
        var challenges = new FakeTwoFactorChallengeRepository();
        var refreshTokens = new RecordingRefreshTokenRepository();
        var eventBus = new RecordingEventBus();
        var sut = CreateSut(new StubUserRepository(user), challenges, refreshTokens, eventBus);

        // Act
        var result = await sut.LoginAsync(new LoginRequest(user.Email, CorrectPassword));

        // Assert: 2FA is required and NO tokens are issued yet (main step 6).
        Assert.True(result.RequiresTwoFactor);
        Assert.Null(result.Tokens);
        Assert.Equal(user.Email, result.Email);
        Assert.Null(refreshTokens.Added);

        // Step 4: a challenge is stored, hashed, unused, with a short future expiry.
        Assert.NotNull(challenges.Added);
        Assert.Equal($"H:{GeneratedCode}", challenges.Added!.CodeHash);
        Assert.NotEqual(GeneratedCode, challenges.Added.CodeHash); // never the plaintext
        Assert.Equal(0, challenges.Added.AttemptCount);
        Assert.True(challenges.Added.ExpiresAtUtc > DateTime.UtcNow);
        Assert.True(challenges.Added.ExpiresAtUtc <= DateTime.UtcNow.AddMinutes(6));

        // Step 5: the plaintext code is emailed exactly once.
        var message = Assert.Single(eventBus.Published.OfType<SendTwoFactorCodeMessage>());
        Assert.Equal(user.Email, message.Email);
        Assert.Equal(GeneratedCode, message.Code);
    }

    [Fact]
    public async Task LoginAsync_NonSuperAdminWithValidCredentials_IssuesTokensWithoutChallengeOrEmail()
    {
        // Arrange: an ordinary active user should keep the original single-stage login.
        var user = ActiveUser(RoleName.Teacher);
        var challenges = new FakeTwoFactorChallengeRepository();
        var refreshTokens = new RecordingRefreshTokenRepository();
        var eventBus = new RecordingEventBus();
        var sut = CreateSut(new StubUserRepository(user), challenges, refreshTokens, eventBus);

        // Act
        var result = await sut.LoginAsync(new LoginRequest(user.Email, CorrectPassword));

        // Assert: tokens issued immediately, no 2FA, no challenge, no email.
        Assert.False(result.RequiresTwoFactor);
        Assert.NotNull(result.Tokens);
        Assert.False(string.IsNullOrEmpty(result.Tokens!.AccessToken));
        Assert.NotNull(refreshTokens.Added);
        Assert.Null(challenges.Added);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task LoginAsync_WithInvalidPassword_ThrowsAndSendsNoCode()
    {
        // Alternate path 2أ: wrong password -> generic error, no code generated or sent.
        var user = ActiveSuperAdmin();
        var challenges = new FakeTwoFactorChallengeRepository();
        var eventBus = new RecordingEventBus();
        var sut = CreateSut(new StubUserRepository(user), challenges, new RecordingRefreshTokenRepository(), eventBus);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sut.LoginAsync(new LoginRequest(user.Email, "wrong-password")));

        Assert.Null(challenges.Added);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task LoginAsync_WithUnknownEmail_ThrowsAndSendsNoCode()
    {
        // Alternate path 2أ: unknown email is rejected identically to a wrong password.
        var challenges = new FakeTwoFactorChallengeRepository();
        var eventBus = new RecordingEventBus();
        var sut = CreateSut(new StubUserRepository(null), challenges, new RecordingRefreshTokenRepository(), eventBus);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sut.LoginAsync(new LoginRequest("nobody@intellilect.io", CorrectPassword)));

        Assert.Null(challenges.Added);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task LoginAsync_WhenSuperAdminAccountDeactivated_ThrowsAndSendsNoCode()
    {
        // Alternate path 2ب: inactive account is stopped before any code is issued.
        var user = ActiveSuperAdmin();
        user.Deactivate();
        var challenges = new FakeTwoFactorChallengeRepository();
        var eventBus = new RecordingEventBus();
        var sut = CreateSut(new StubUserRepository(user), challenges, new RecordingRefreshTokenRepository(), eventBus);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sut.LoginAsync(new LoginRequest(user.Email, CorrectPassword)));

        Assert.Null(challenges.Added);
        Assert.Empty(eventBus.Published);
    }

    // ----- Stage 2: VerifyTwoFactorAsync --------------------------------------

    [Fact]
    public async Task VerifyTwoFactorAsync_WithValidCode_InvalidatesChallengeAndIssuesTwoFactorMarkedSession()
    {
        // Arrange: a live challenge matching the submitted code (main steps 7-10).
        var user = ActiveSuperAdmin();
        var challenge = ChallengeFor(user, GeneratedCode, DateTime.UtcNow.AddMinutes(5));
        var challenges = new FakeTwoFactorChallengeRepository(challenge);
        var refreshTokens = new RecordingRefreshTokenRepository();
        var jwt = new RecordingJwtProvider();
        var sut = CreateSut(new StubUserRepository(user), challenges, refreshTokens, new RecordingEventBus(), jwt);

        // Act
        var response = await sut.VerifyTwoFactorAsync(new VerifyTwoFactorRequest(user.Email, GeneratedCode));

        // Assert: tokens issued, refresh token stored (main step 10).
        Assert.False(string.IsNullOrEmpty(response.AccessToken));
        Assert.False(string.IsNullOrEmpty(response.RefreshToken));
        Assert.NotNull(refreshTokens.Added);
        Assert.Equal(1, refreshTokens.SaveChangesCallCount);

        // Step 9: the single-use code is invalidated so it cannot be replayed.
        Assert.True(challenges.Deleted);

        // Postcondition: the access token is marked as having completed 2FA.
        Assert.True(jwt.LastTwoFactorCompleted);
    }

    [Fact]
    public async Task VerifyTwoFactorAsync_WhenNoChallengeExists_ThrowsAndIssuesNoSession()
    {
        // Alternate path 8أ: no challenge on record -> restart login.
        var user = ActiveSuperAdmin();
        var challenges = new FakeTwoFactorChallengeRepository(null);
        var refreshTokens = new RecordingRefreshTokenRepository();
        var sut = CreateSut(new StubUserRepository(user), challenges, refreshTokens, new RecordingEventBus());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sut.VerifyTwoFactorAsync(new VerifyTwoFactorRequest(user.Email, GeneratedCode)));

        Assert.Null(refreshTokens.Added);
    }

    [Fact]
    public async Task VerifyTwoFactorAsync_WhenChallengeExpired_DeletesChallengeAndThrows()
    {
        // Alternate path 8أ: an expired code is rejected and the stale record is burned.
        var user = ActiveSuperAdmin();
        var challenge = ChallengeFor(user, GeneratedCode, DateTime.UtcNow.AddMinutes(-1));
        var challenges = new FakeTwoFactorChallengeRepository(challenge);
        var refreshTokens = new RecordingRefreshTokenRepository();
        var sut = CreateSut(new StubUserRepository(user), challenges, refreshTokens, new RecordingEventBus());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sut.VerifyTwoFactorAsync(new VerifyTwoFactorRequest(user.Email, GeneratedCode)));

        Assert.True(challenges.Deleted);
        Assert.Null(refreshTokens.Added);
    }

    [Fact]
    public async Task VerifyTwoFactorAsync_WithWrongCode_IncrementsAttemptsAndKeepsChallenge()
    {
        // Alternate path 8ب: wrong code -> attempt counted, challenge preserved for retry.
        var user = ActiveSuperAdmin();
        var challenge = ChallengeFor(user, GeneratedCode, DateTime.UtcNow.AddMinutes(5));
        var challenges = new FakeTwoFactorChallengeRepository(challenge);
        var refreshTokens = new RecordingRefreshTokenRepository();
        var sut = CreateSut(new StubUserRepository(user), challenges, refreshTokens, new RecordingEventBus());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sut.VerifyTwoFactorAsync(new VerifyTwoFactorRequest(user.Email, "000000")));

        Assert.Equal(1, challenge.AttemptCount);
        Assert.False(challenges.Deleted);
        Assert.Null(refreshTokens.Added);
    }

    [Fact]
    public async Task VerifyTwoFactorAsync_WhenWrongCodeReachesMaxAttempts_InvalidatesChallengeAndThrows()
    {
        // Alternate path 8ج: the final wrong attempt trips the limit and burns the challenge.
        var user = ActiveSuperAdmin();
        var challenge = ChallengeFor(user, GeneratedCode, DateTime.UtcNow.AddMinutes(5));
        challenge.AttemptCount = TwoFactorChallenge.MaxAttempts - 1; // one attempt left
        var challenges = new FakeTwoFactorChallengeRepository(challenge);
        var refreshTokens = new RecordingRefreshTokenRepository();
        var sut = CreateSut(new StubUserRepository(user), challenges, refreshTokens, new RecordingEventBus());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sut.VerifyTwoFactorAsync(new VerifyTwoFactorRequest(user.Email, "000000")));

        Assert.True(challenge.HasExceededMaxAttempts);
        Assert.True(challenges.Deleted);
        Assert.Null(refreshTokens.Added);
    }

    [Fact]
    public async Task VerifyTwoFactorAsync_WithUnknownEmail_ThrowsAndIssuesNoSession()
    {
        // Unknown email is treated like a missing code, without revealing account existence.
        var challenges = new FakeTwoFactorChallengeRepository(null);
        var refreshTokens = new RecordingRefreshTokenRepository();
        var sut = CreateSut(new StubUserRepository(null), challenges, refreshTokens, new RecordingEventBus());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sut.VerifyTwoFactorAsync(new VerifyTwoFactorRequest("ghost@intellilect.io", GeneratedCode)));

        Assert.Null(refreshTokens.Added);
    }

    // ----- helpers -------------------------------------------------------------

    private static AuthService CreateSut(
        StubUserRepository users,
        FakeTwoFactorChallengeRepository challenges,
        RecordingRefreshTokenRepository refreshTokens,
        RecordingEventBus eventBus,
        RecordingJwtProvider? jwt = null)
        => new(
            users,
            new NotUsedRepository<Role>(),
            new NotUsedRoleRepository(),
            new FakeHasher(),
            jwt ?? new RecordingJwtProvider(),
            refreshTokens,
            resetPasswordRepository: null!,
            resetPasswordTokenGenerator: null!,
            twoFactorRepository: challenges,
            twoFactorCodeGenerator: new StubTwoFactorCodeGenerator(GeneratedCode),
            mapper: BuildMapper(),
            eventBus: eventBus);

    private static IMapper BuildMapper()
    {
        var config = new MapperConfiguration(cfg => cfg.AddProfile<UserProfile>());
        return config.CreateMapper();
    }

    private static User ActiveSuperAdmin(string email = "superadmin@intellilect.io")
        => ActiveUser(RoleName.SuperAdmin, email);

    private static User ActiveUser(RoleName role, string email = "user@intellilect.io")
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = role.ToString().ToLowerInvariant(),
            FirstName = "First",
            LastName = "Last",
            PasswordHash = $"H:{CorrectPassword}",
            RoleId = Guid.NewGuid(),
            Role = Role.Create(role),
        };
        user.Approve(); // move from Pending to Active
        return user;
    }

    private static TwoFactorChallenge ChallengeFor(User user, string code, DateTime expiresAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        CodeHash = $"H:{code}",
        ExpiresAtUtc = expiresAtUtc,
        AttemptCount = 0,
        CreatedAtUtc = DateTime.UtcNow,
        UserId = user.Id,
    };
}

// ----- fakes ------------------------------------------------------------------
// Only the members the two-factor flow exercises are implemented; anything else throws
// so an accidental dependency surfaces immediately. Names are distinct from the logout
// test's fakes to avoid clashes within the same test assembly.

internal sealed class StubUserRepository : IUserRepository
{
    private readonly User? _user;
    public StubUserRepository(User? user) => _user = user;

    public Task<User?> FindByEmail(string email, CancellationToken cancellationToken = default) => Task.FromResult(_user);

    public Task<User?> GetByIdWithRefreshTokensAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<User?> FindByRefreshToken(string token, CancellationToken ct) => throw new NotImplementedException();
    public Task<User?> FindByResetToken(string token, CancellationToken ct) => throw new NotImplementedException();
    public Task<(List<User> Items, int TotalCount)> GetPaginatedPendingUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct) => throw new NotImplementedException();
    public Task<(List<User> Items, int TotalCount)> GetPaginatedUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct) => throw new NotImplementedException();
    public Task<(List<User> Items, int TotalCount)> SearchUsersAsync(UserManagementService.Application.Common.Users.UserQuerySpecification specification, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task AddAsync(User entity, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task UpdateAsync(User entity, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
}

internal sealed class FakeTwoFactorChallengeRepository : ITwoFactorChallengeRepository
{
    private TwoFactorChallenge? _current;

    public FakeTwoFactorChallengeRepository(TwoFactorChallenge? current = null) => _current = current;

    public TwoFactorChallenge? Added { get; private set; }
    public bool Deleted { get; private set; }
    public int SaveChangesCallCount { get; private set; }

    public Task<TwoFactorChallenge?> FindByUserId(Guid userId, CancellationToken ct = default) => Task.FromResult(_current);

    public Task AddAsync(TwoFactorChallenge entity, CancellationToken cancellationToken = default)
    {
        Added = entity;
        _current = entity;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(TwoFactorChallenge entity, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Deleted = true;
        _current = null;
        return Task.CompletedTask;
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveChangesCallCount++;
        return Task.FromResult(1);
    }

    public Task<TwoFactorChallenge?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
}

internal sealed class RecordingRefreshTokenRepository : IRepository<RefreshToken>
{
    public RefreshToken? Added { get; private set; }
    public int SaveChangesCallCount { get; private set; }

    public Task AddAsync(RefreshToken entity, CancellationToken cancellationToken = default)
    {
        Added = entity;
        return Task.CompletedTask;
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveChangesCallCount++;
        return Task.FromResult(1);
    }

    public Task<RefreshToken?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task UpdateAsync(RefreshToken entity, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
}

internal sealed class RecordingEventBus : IEventBus
{
    public List<object> Published { get; } = new();

    public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class
    {
        Published.Add(message);
        return Task.CompletedTask;
    }
}

// Deterministic hasher: Hash(x) = "H:x"; Verify(plain, hash) = hash equals "H:plain".
internal sealed class FakeHasher : IHasher
{
    public string Hash(string code) => $"H:{code}";
    public bool Verify(string oldCode, string codeHash) => codeHash == $"H:{oldCode}";
}

internal sealed class StubTwoFactorCodeGenerator : ITwoFactorCodeGenerator
{
    private readonly string _code;
    public StubTwoFactorCodeGenerator(string code) => _code = code;
    public string Generate() => _code;
}

internal sealed class RecordingJwtProvider : IJwtProvider
{
    public bool LastTwoFactorCompleted { get; private set; }

    public string GenerateAccessToken(Guid userId, string roleName, string userName, bool twoFactorCompleted = false)
    {
        LastTwoFactorCompleted = twoFactorCompleted;
        return "access-token";
    }

    public string GenerateRefreshToken() => "refresh-token";
}
