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

    public AuthService(
        IUserRepository userRepository,
        IRepository<Role> roleRepository,
        IHasher hasher,
        IJwtProvider jwtProvider)
    {
        _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
        _roleRepository = roleRepository ?? throw new ArgumentNullException(nameof(roleRepository));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _jwtProvider = jwtProvider ?? throw new ArgumentNullException(nameof(jwtProvider));
    }

    public async Task<Guid> RegisterAsync(
        RegisterRequest request,
        CancellationToken cancellationToken = default)
    {
        // Basic Validation (You can later move this to FluentValidation)
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
        var user = User.Create(
            userName: request.UserName,
            email: request.Email,
            firstName: request.FirstName,
            lastName: request.LastName,
            roleId: request.RoleId,
            passwordHash: passwordHash,
            role: role);

        await _userRepository.AddAsync(user, cancellationToken);

        return user.Id;
    }

    public async Task<AuthResponse> AuthenticateAsync(
        LoginRequest request,
        CancellationToken cancellationToken = default)
    {
        // Find user by email
        var user = await _userRepository.FindByEmail(request.Email, cancellationToken);
        if (user == null)
            throw new InvalidOperationException("Invalid email or password.");

        // Verify password
        if (!_hasher.VerifyPassword(request.Password, user.PasswordHash))
            throw new InvalidOperationException("Invalid email or password.");

        // Generate JWT token 
        var token = _jwtProvider.GenerateToken(
            user.Id, 
            user.RoleId, 
            DateTime.UtcNow.AddHours(1));

        // Return the full AuthResponse DTO
        return new AuthResponse(
            Token: token,
            UserId: user.Id,
            Email: user.Email
        );
    }
}