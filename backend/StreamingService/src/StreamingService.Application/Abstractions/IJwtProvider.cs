namespace StreamingService.Application.Abstractions;

public interface IJwtProvider
{
    string GenerateAccessToken(Guid userId, string roleName);
    string GenerateRefreshToken();
}