using AutoMapper;
using IntelliLect.Contracts.Messages;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.DTOs;
using UserManagementService.Application.DTOs.Auth;
using UserManagementService.Application.DTOs.User;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Application.Authentication;

public sealed class AuthService : IAuthService
{
    private readonly IUserRepository _userRepository;
    private readonly IRepository<Role> _roleRepository;
    private readonly IHasher _hasher;
    private readonly IJwtProvider _jwtProvider;
    private readonly IRepository<RefreshToken> _refreshTokenRepository;
    private readonly IResetTokenRepository _resetPasswordRepository;
    private readonly IResetPasswordTokenGenerator _resetPasswordTokenGenerator;
    private readonly ITwoFactorChallengeRepository _twoFactorRepository;
    private readonly ITwoFactorCodeGenerator _twoFactorCodeGenerator;
    private readonly IEventBus _eventBus;
    private readonly IMapper _mapper;
    private readonly IRoleRepository _roleQueryRepository;

    // Two-factor codes are short-lived to limit the window for interception/guessing.
    private static readonly TimeSpan TwoFactorCodeLifetime = TimeSpan.FromMinutes(5);


    public AuthService(
    IUserRepository userRepository,
    IRepository<Role> roleRepository,
    IRoleRepository roleQueryRepository,
    IHasher hasher,
    IJwtProvider jwtProvider,
    IRepository<RefreshToken> refreshTokenRepository,
    IResetTokenRepository resetPasswordRepository,
    IResetPasswordTokenGenerator resetPasswordTokenGenerator,
    ITwoFactorChallengeRepository twoFactorRepository,
    ITwoFactorCodeGenerator twoFactorCodeGenerator,
    IMapper mapper,
    IEventBus eventBus)
    {
        _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
        _roleRepository = roleRepository ?? throw new ArgumentNullException(nameof(roleRepository));
        _roleQueryRepository = roleQueryRepository ?? throw new ArgumentNullException(nameof(roleQueryRepository));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _jwtProvider = jwtProvider ?? throw new ArgumentNullException(nameof(jwtProvider));
        _refreshTokenRepository = refreshTokenRepository;
        _resetPasswordRepository = resetPasswordRepository;
        _resetPasswordTokenGenerator = resetPasswordTokenGenerator;
        _twoFactorRepository = twoFactorRepository;
        _twoFactorCodeGenerator = twoFactorCodeGenerator;
        _mapper = mapper;
        _eventBus = eventBus;
    }

    public async Task<Guid> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var role = await _roleRepository.GetByIdAsync(request.RoleId, ct);
        if (role == null) throw new ArgumentException("The selected role is invalid.");
        if (role.Name is RoleName.Admin or RoleName.SuperAdmin)
            throw new ArgumentException("The selected role is not available for self-registration.");

        var existingUser = await _userRepository.FindByEmail(request.Email, ct);

        if (existingUser != null)
        {
            throw new InvalidOperationException("A user with this email already exists.");
        }

        var user = _mapper.Map<User>(request);
        user.UpdatePassword(_hasher.Hash(request.Password));

        await _userRepository.AddAsync(user, ct);
        await _eventBus.PublishAsync(new UserStatusChangedMessage(user.Email, user.FirstName, UserStatus.Pending.ToString()), ct);

        await _userRepository.SaveChangesAsync(ct);

        return user.Id;
    }

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        // Step 1/2: Find the user (repository includes the Role entity) and verify the
        // credentials first. Alternate path 2أ: a wrong email or password yields the same
        // generic error so the response never reveals whether an email is registered.
        var user = await _userRepository.FindByEmail(request.Email, ct);

        if (user == null || !_hasher.Verify(request.Password, user.PasswordHash))
            throw new UnauthorizedAccessException("Invalid credentials.");

        // Step 2: the account must be active. Alternate path 2ب: an inactive account is told
        // its status and stopped here, before any code is generated or sent.
        switch (user.Status)
        {
            case UserStatus.Pending:
                throw new UnauthorizedAccessException("Account pending approval. Please wait for an administrator to activate your account.");

            case UserStatus.Rejected:
                throw new UnauthorizedAccessException("Your registration request was rejected.");

            case UserStatus.Deactivated:
                throw new UnauthorizedAccessException("This account has been deactivated.");

            case UserStatus.Active:
                break; // Proceed

            default:
                throw new UnauthorizedAccessException("Account is in an invalid state.");
        }

        // Step 3: super admins carry sensitive privileges, so their login always requires a
        // second factor. For every other role, issue tokens immediately (unchanged behaviour).
        if (user.Role.Name == RoleName.SuperAdmin)
        {
            await IssueTwoFactorChallengeAsync(user, ct);
            return LoginResult.TwoFactorRequired(user.Email);
        }

        var tokens = await IssueSessionAsync(user, twoFactorCompleted: false, ct);
        return LoginResult.Authenticated(tokens);
    }

    public async Task<LoginResponse> VerifyTwoFactorAsync(VerifyTwoFactorRequest request, CancellationToken ct = default)
    {
        // Resolve the account. A missing user is reported exactly like a missing/expired code
        // (Alternate path 8أ) so this endpoint cannot be used to probe for registered emails.
        var user = await _userRepository.FindByEmail(request.Email, ct);
        if (user == null)
            throw new UnauthorizedAccessException("Verification code expired or not found. Please sign in again.");

        var challenge = await _twoFactorRepository.FindByUserId(user.Id, ct);

        // Alternate path 8أ: no active challenge, or it has expired. Burn any stale record and
        // ask the user to restart the login to obtain a fresh code.
        if (challenge == null || challenge.IsExpired)
        {
            if (challenge != null)
            {
                await _twoFactorRepository.DeleteAsync(challenge.Id, ct);
                await _twoFactorRepository.SaveChangesAsync(ct);
            }

            throw new UnauthorizedAccessException("Verification code expired or not found. Please sign in again.");
        }

        // Alternate path 8ب: wrong code. Count the failed attempt, and on reaching the limit
        // (Alternate path 8ج) invalidate the challenge so the login must be started over.
        if (!_hasher.Verify(request.Code, challenge.CodeHash))
        {
            challenge.RegisterFailedAttempt();

            if (challenge.HasExceededMaxAttempts)
            {
                await _twoFactorRepository.DeleteAsync(challenge.Id, ct);
                await _twoFactorRepository.SaveChangesAsync(ct);
                throw new UnauthorizedAccessException("Too many incorrect attempts. Please sign in again.");
            }

            await _twoFactorRepository.UpdateAsync(challenge, ct);
            await _twoFactorRepository.SaveChangesAsync(ct);
            throw new UnauthorizedAccessException("Invalid verification code.");
        }

        // Step 9: the code is correct and single-use, so remove the challenge before issuing
        // the session; it can never be replayed. Step 10: mint the 2FA-marked access token and
        // a refresh token. The delete and the new refresh token share one DbContext transaction.
        await _twoFactorRepository.DeleteAsync(challenge.Id, ct);

        return await IssueSessionAsync(user, twoFactorCompleted: true, ct);
    }

    // Creates (or refreshes) the single pending challenge for a user: a fresh six-digit code
    // stored only as a hash, with a short expiry and a reset attempt counter (main steps 4-5).
    private async Task IssueTwoFactorChallengeAsync(User user, CancellationToken ct)
    {
        var code = _twoFactorCodeGenerator.Generate();
        var codeHash = _hasher.Hash(code);
        var expiresAtUtc = DateTime.UtcNow.Add(TwoFactorCodeLifetime);

        var existing = await _twoFactorRepository.FindByUserId(user.Id, ct);
        if (existing == null)
        {
            await _twoFactorRepository.AddAsync(new TwoFactorChallenge
            {
                Id = Guid.NewGuid(),
                CodeHash = codeHash,
                ExpiresAtUtc = expiresAtUtc,
                AttemptCount = 0,
                CreatedAtUtc = DateTime.UtcNow,
                UserId = user.Id
            }, ct);
        }
        else
        {
            // A previous, unused challenge exists (e.g. the user restarted login). Replace its
            // code and reset expiry/attempts so only the latest emailed code is ever valid.
            existing.Refresh(codeHash, expiresAtUtc);
            await _twoFactorRepository.UpdateAsync(existing, ct);
        }

        await _twoFactorRepository.SaveChangesAsync(ct);

        // Step 5: deliver the code out-of-band via the Email service.
        await _eventBus.PublishAsync(new SendTwoFactorCodeMessage(user.Email, code), ct);
    }

    // Issues a session: a refresh token (persisted) plus an access token, optionally marked as
    // having completed two-factor authentication.
    private async Task<LoginResponse> IssueSessionAsync(User user, bool twoFactorCompleted, CancellationToken ct)
    {
        var accessToken = _jwtProvider.GenerateAccessToken(
            user.Id, user.Role.Name.ToString(), user.UserName, twoFactorCompleted);
        var refreshToken = _jwtProvider.GenerateRefreshToken();

        await _refreshTokenRepository.AddAsync(new RefreshToken()
        {
            Id = Guid.NewGuid(),
            Token = refreshToken,
            IsRevoked = false,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
            UserId = user.Id
        }, ct);

        await _refreshTokenRepository.SaveChangesAsync(ct);

        var response = _mapper.Map<UserResponse>(user);

        return new LoginResponse(accessToken, refreshToken, response);
    }

    public async Task<LoginResponse> RefreshAsync(RefreshTokenRequest request, CancellationToken ct)
    {
        var user = await _userRepository.FindByRefreshToken(request.RefreshToken, ct);

        // Find the specific token record in the user's collection
        var tokenRecord = user?.RefreshTokens.FirstOrDefault(rt => rt.Token == request.RefreshToken);

        if (user == null || tokenRecord == null || !tokenRecord.IsActive)
            throw new UnauthorizedAccessException("Invalid or expired refresh token.");

        // Revoke the old token (Token Rotation)
        tokenRecord.Revoke();

        // A super admin's refresh token is only ever issued after two-factor verification, so
        // the rotated access token must keep the 2FA-completed marking.
        var twoFactorCompleted = user.Role.Name == RoleName.SuperAdmin;
        var newAccess = _jwtProvider.GenerateAccessToken(user.Id, user.Role.Name.ToString(), user.UserName, twoFactorCompleted);
        var newRefresh = _jwtProvider.GenerateRefreshToken();

        await _refreshTokenRepository.AddAsync(new RefreshToken()
        {
            Id = Guid.NewGuid(),
            Token = newRefresh,
            IsRevoked = false,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
            User = user
        });

        await _refreshTokenRepository.SaveChangesAsync(ct);

        var response = _mapper.Map<UserResponse>(user);

        return new LoginResponse(newAccess, newRefresh, response);
    }

    public async Task LogoutAsync(Guid userId, string refreshToken, CancellationToken ct = default)
    {
        // Step 3: Locate the refresh token tied to the session.
        var user = await _userRepository.FindByRefreshToken(refreshToken, ct);
        var tokenRecord = user?.RefreshTokens.FirstOrDefault(rt => rt.Token == refreshToken);

        // Alternate path 3: token missing or already revoked. The goal (session can no
        // longer be renewed) is already met, so treat logout as successful and do nothing.
        if (tokenRecord is null || tokenRecord.IsRevoked)
            return;

        // Step 4 / Alternate path 4: refuse to revoke a token that belongs to another user,
        // preventing one user from ending another user's session.
        if (tokenRecord.UserId != userId)
            throw new UnauthorizedAccessException("The refresh token does not belong to the current user.");

        // Step 5: revoke the token so it can no longer issue new access tokens.
        tokenRecord.Revoke();
        await _refreshTokenRepository.SaveChangesAsync(ct);
    }

    public async Task ForgotPasswordAsync(string email, CancellationToken ct)
    {
        var user = await _userRepository.FindByEmail(email, ct);

        if (user == null) return;

        var resetPasswordToken = await _resetPasswordRepository.FindResetPasswordTokenByUserId(user.Id);

        var code = _resetPasswordTokenGenerator.Generate();

        if (resetPasswordToken == null)
        {
            resetPasswordToken = new ResetPasswordToken()
            {
                Id = Guid.NewGuid(),
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15),
                Token = _hasher.Hash(code),
                RequestCount = 1,
                LastRequestedAtUtc = DateTime.UtcNow,
                UserId = user.Id,
                User = user
            };

            await _resetPasswordRepository.AddAsync(resetPasswordToken, ct);
        }
        else
        {
            var isCurrentlyBlocked = resetPasswordToken.LastRequestedAtUtc.HasValue &&
                                 resetPasswordToken.LastRequestedAtUtc.Value.AddDays(1) > DateTime.UtcNow &&
                                 resetPasswordToken.RequestCount >= 5;

            if (isCurrentlyBlocked)
            {
                throw new InvalidOperationException("Daily limit reached. Please try again in 24 hours.");
            }

            resetPasswordToken.UpdateToken(_hasher.Hash(code));

            await _resetPasswordRepository.UpdateAsync(resetPasswordToken, ct);
        }

        await _eventBus.PublishAsync(new SendResetCodeMessage(email, code), ct);
        await _resetPasswordRepository.SaveChangesAsync(ct);

        // Logging the result
        Console.WriteLine($"[AUTH] Reset code for {email}: {code} (Attempt {resetPasswordToken.RequestCount}/5)");
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct)
    {
        var user = await _userRepository.FindByEmail(request.Email, ct);

        if (user == null)
            throw new ArgumentException("User not found.");

        var resetPasswordToken = await _resetPasswordRepository.FindResetPasswordTokenByUserId(user.Id);

        if (resetPasswordToken == null)
            throw new InvalidOperationException("Token not found.");

        if (resetPasswordToken.IsExpired || !_hasher.Verify(request.Token, resetPasswordToken.Token))
            throw new InvalidOperationException("Invalid or expired reset token.");

        await _resetPasswordRepository.DeleteAsync(resetPasswordToken.Id);

        await _resetPasswordRepository.SaveChangesAsync(ct);

        user.PasswordHash = _hasher.Hash(request.NewPassword);

        await _userRepository.UpdateAsync(user, ct);

        await _userRepository.SaveChangesAsync(ct);
    }
    public async Task<IReadOnlyList<RegistrationRoleResponse>> GetRegistrationRolesAsync(CancellationToken ct = default)
    {
        var roles = await _roleQueryRepository.GetSelfRegistrationRolesAsync(ct);

        return roles
            .Select(role => new RegistrationRoleResponse(
                role.Id,
                role.Name.ToString()))
            .ToList();
    }
}
