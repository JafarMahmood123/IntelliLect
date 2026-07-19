using UserManagementService.Application.DTOs;
using UserManagementService.Application.DTOs.Auth;

namespace UserManagementService.Application.Abstractions;

public interface IAuthService
{
    Task<IReadOnlyList<RegistrationRoleResponse>> GetRegistrationRolesAsync(CancellationToken ct = default);
    Task<Guid> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
    Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task<LoginResponse> RefreshAsync(RefreshTokenRequest request, CancellationToken ct = default);
    Task LogoutAsync(Guid userId, string refreshToken, CancellationToken ct = default);
    Task ForgotPasswordAsync(string email, CancellationToken ct = default);
    Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct = default);
}