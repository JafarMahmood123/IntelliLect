using AutoMapper;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.DTOs;
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
        // Basic Validation
        if (string.IsNullOrWhiteSpace(request.Email)) throw new ArgumentException("Email is required.");

        // Check if user already exists
        var existingUser = await _userRepository.FindByEmail(request.Email, cancellationToken);
        if (existingUser != null)
            throw new InvalidOperationException("A user with this email already exists.");

        // Validate Role
        var role = await _roleRepository.GetByIdAsync(request.RoleId, cancellationToken);
        if (role == null)
            throw new ArgumentException("Invalid role specified.");

        // Hash the password
        var passwordHash = _hasher.HashPassword(request.Password);

        // Map DTO to Domain Entity
        var user = _mapper.Map<User>(request);
        user.PasswordHash = passwordHash;

        await _userRepository.AddAsync(user, cancellationToken);

        await _userRepository.SaveChangesAsync(cancellationToken);

        return user.Id;
    }

    public async Task<AuthResponse> AuthenticateAsync(LoginRequest request, CancellationToken ct = default)
    {
        var user = await _userRepository.FindByEmail(request.Email, ct);
        if (user == null || !_hasher.VerifyPassword(request.Password, user.PasswordHash))
            throw new UnauthorizedAccessException("Invalid credentials.");

        var accessToken = _jwtProvider.GenerateAccessToken(user.Id, user.RoleId);
        var refreshToken = _jwtProvider.GenerateRefreshToken();

        await _refreshTokenRepository.AddAsync(new RefreshToken()
        {
            Id = Guid.NewGuid(),
            Token = refreshToken,
            IsRevoked = false,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
            User = user
        });

        await _refreshTokenRepository.SaveChangesAsync();

        return new AuthResponse(accessToken, refreshToken, user.Id);
    }

    public async Task<AuthResponse> RefreshAsync(RefreshTokenRequest request, CancellationToken ct)
    {
        var user = await _userRepository.FindByRefreshToken(request.RefreshToken, ct);

        // Find the specific token record in the user's collection
        var tokenRecord = user?.RefreshTokens.FirstOrDefault(rt => rt.Token == request.RefreshToken);

        if (user == null || tokenRecord == null || !tokenRecord.IsActive)
            throw new UnauthorizedAccessException("Invalid or expired refresh token.");

        // Revoke the old token (Token Rotation)
        tokenRecord.Revoke();

        var newAccess = _jwtProvider.GenerateAccessToken(user.Id, user.RoleId);
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

        return new AuthResponse(newAccess, newRefresh, user.Id);
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