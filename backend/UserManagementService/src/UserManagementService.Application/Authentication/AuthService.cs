using AutoMapper;
using EmailService.Contracts.Messages;
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
    private readonly IEventBus _eventBus;
    private readonly IMapper _mapper;


    public AuthService(
        IUserRepository userRepository,
        IRepository<Role> roleRepository,
        IHasher hasher,
        IJwtProvider jwtProvider,
        IRepository<RefreshToken> refreshTokenRepository,
        IResetTokenRepository resetPasswordRepository,
        IResetPasswordTokenGenerator resetPasswordTokenGenerator,
        IMapper mapper,
        IEventBus eventBus)
    {
        _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
        _roleRepository = roleRepository ?? throw new ArgumentNullException(nameof(roleRepository));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _jwtProvider = jwtProvider ?? throw new ArgumentNullException(nameof(jwtProvider));
        _refreshTokenRepository = refreshTokenRepository;
        _resetPasswordRepository = resetPasswordRepository;
        _resetPasswordTokenGenerator = resetPasswordTokenGenerator;
        _mapper = mapper;
        _eventBus = eventBus;
    }

    public async Task<Guid> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var existingUser = await _userRepository.FindByEmail(request.Email);

        if (existingUser != null)
        {
            if (!existingUser.IsDeleted)
                throw new InvalidOperationException("A user with this email already exists.");

            existingUser.UpdateInfo(request.FirstName, request.LastName, request.UserName, null);
            existingUser.UpdatePassword(_hasher.Hash(request.Password));

            existingUser.Restore(request.RoleId);

            await _eventBus.PublishAsync(new UserStatusChangedMessage(existingUser.Email, existingUser.FirstName, UserStatus.Pending.ToString()), ct);

            await _userRepository.UpdateAsync(existingUser, ct);
            await _userRepository.SaveChangesAsync(ct);
            return existingUser.Id;
        }

        var role = await _roleRepository.GetByIdAsync(request.RoleId, ct);
        if (role == null) throw new ArgumentException("The selected role is invalid.");

        var user = _mapper.Map<User>(request);
        user.UpdatePassword(_hasher.Hash(request.Password));

        await _userRepository.AddAsync(user, ct);
        await _eventBus.PublishAsync(new UserStatusChangedMessage(user.Email, user.FirstName, UserStatus.Pending.ToString()), ct);

        await _userRepository.SaveChangesAsync(ct);

        return user.Id;
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        // 1. Find the user (Ensure the repository includes the Role entity)
        var user = await _userRepository.FindByEmail(request.Email, ct);

        // 2. Check credentials first (to prevent account enumeration attacks)
        if (user == null || !_hasher.Verify(request.Password, user.PasswordHash))
            throw new UnauthorizedAccessException("Invalid credentials.");

        // 3. Check Account Status
        // We use the UserStatus enum we created earlier
        switch (user.Status)
        {
            case UserStatus.Pending:
                throw new UnauthorizedAccessException("Account pending approval. Please wait for an administrator to activate your account.");

            case UserStatus.Rejected:
                throw new UnauthorizedAccessException("Your registration request was rejected.");

            case UserStatus.Deactivated:
                throw new UnauthorizedAccessException("This account has been deactivated.");

            case UserStatus.Active:
                break; // Proceed to token generation

            default:
                throw new UnauthorizedAccessException("Account is in an invalid state.");
        }

        // 4. Generate Tokens
        // We pass the Role Name (string) as well if your JwtProvider supports it
        var accessToken = _jwtProvider.GenerateAccessToken(user.Id, user.Role.Name.ToString());
        var refreshToken = _jwtProvider.GenerateRefreshToken();

        // 5. Store Refresh Token
        await _refreshTokenRepository.AddAsync(new RefreshToken()
        {
            Id = Guid.NewGuid(),
            Token = refreshToken,
            IsRevoked = false,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
            UserId = user.Id // Explicitly set UserId
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

        var newAccess = _jwtProvider.GenerateAccessToken(user.Id, user.Role.Name.ToString());
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
}
