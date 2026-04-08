namespace ClassroomService.Application.Abstractions;

public interface IJwtProvider
{
    string GenerateAccessToken(Guid userId, string roleName); // Changed Guid roleId to string roleName
    string GenerateRefreshToken();
}