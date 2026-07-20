namespace UserManagementService.Application.Abstractions;

public interface IJwtProvider
{
    string GenerateAccessToken(Guid userId, string roleName, string userName, bool twoFactorCompleted = false);
    string GenerateRefreshToken();
}