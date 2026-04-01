public interface IJwtProvider
{
    string GenerateAccessToken(Guid userId, Guid roleId);
    string GenerateRefreshToken();
}