using AutoMapper;
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
    private readonly IRepository<ResetPasswordToken> _resetPasswordRepository;
    private readonly IMapper _mapper;


    public AuthService(
        IUserRepository userRepository,
        IRepository<Role> roleRepository,
        IHasher hasher,
        IJwtProvider jwtProvider,
        IRepository<RefreshToken> refreshTokenRepository,
        IRepository<ResetPasswordToken> resetPasswordRepository,
        IMapper mapper)
    {
        _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
        _roleRepository = roleRepository ?? throw new ArgumentNullException(nameof(roleRepository));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _jwtProvider = jwtProvider ?? throw new ArgumentNullException(nameof(jwtProvider));
        _refreshTokenRepository = refreshTokenRepository;
        _resetPasswordRepository = resetPasswordRepository;
        _mapper = mapper;
    }

    public async Task<Guid> RegisterAsync(
    RegisterRequest request,
    CancellationToken cancellationToken = default)
    {
        // 1. Check if email exists
        var existingUser = await _userRepository.FindByEmail(request.Email, cancellationToken);
        if (existingUser != null)
            throw new InvalidOperationException("A user with this email already exists.");

        // 2. Validate that the Role selected by the user actually exists
        var role = await _roleRepository.GetByIdAsync(request.RoleId, cancellationToken);
        if (role == null)
            throw new ArgumentException("The selected role is invalid.");

        // 4. Use Mapping
        var user = _mapper.Map<User>(request);

        // 5. Set the hashed password (which requires the _hasher service)
        user.UpdatePassword(_hasher.HashPassword(request.Password));

        await _userRepository.AddAsync(user, cancellationToken);
        await _userRepository.SaveChangesAsync(cancellationToken);

        return user.Id;
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        // 1. Find the user (Ensure the repository includes the Role entity)
        var user = await _userRepository.FindByEmail(request.Email, ct);

        // 2. Check credentials first (to prevent account enumeration attacks)
        if (user == null || !_hasher.VerifyPassword(request.Password, user.PasswordHash))
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

        await _refreshTokenRepository.SaveChangesAsync();

        var response = _mapper.Map<UserResponse>(user);

        return new LoginResponse(newAccess, newRefresh, response);
    }

    public async Task ForgotPasswordAsync(string email, CancellationToken ct)
    {
        var user = await _userRepository.FindByEmail(email, ct);
        if (user == null) return;

        var token = Guid.NewGuid().ToString("N");
        await _resetPasswordRepository.AddAsync(new ResetPasswordToken()
        {
            Id = Guid.NewGuid(),
            Token = token,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15),
            User = user
        });

        await _resetPasswordRepository.SaveChangesAsync();

        // Log the token for now (In a real app, you'd email this)
        Console.WriteLine($"Password Reset Token for {email}: {token}");
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct)
    {
        var user = await _userRepository.FindByResetToken(request.Token, ct);

        if (user == null || user.ResetPasswordToken!.IsExpired)
            throw new InvalidOperationException("Invalid or expired reset token.");

        user.PasswordHash = _hasher.HashPassword(request.NewPassword);

        await _userRepository.UpdateAsync(user, ct);

        await _userRepository.SaveChangesAsync(ct);
    }
}