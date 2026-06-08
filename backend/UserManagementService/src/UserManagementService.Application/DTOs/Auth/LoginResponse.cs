using UserManagementService.Application.DTOs.User;

namespace UserManagementService.Application.DTOs.Auth
{
    public record LoginResponse(
    string AccessToken,
    string RefreshToken,
    UserResponse Response);
}