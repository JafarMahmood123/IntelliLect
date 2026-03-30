using UserManagementService.Application.DTOs;

public interface IAuthService
{
    Task<Guid> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);
    Task<AuthResponse> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default);
}