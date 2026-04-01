using UserManagementService.Application.DTOs;

public interface IAuthService
{
    Task<Guid> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
    Task<AuthResponse> AuthenticateAsync(LoginRequest request, CancellationToken ct = default);
    Task<AuthResponse> RefreshAsync(RefreshTokenRequest request, CancellationToken ct = default);
    Task ForgotPasswordAsync(string email, CancellationToken ct = default);
    Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct = default);
}