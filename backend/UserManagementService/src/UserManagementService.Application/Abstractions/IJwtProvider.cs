namespace UserManagementService.Application.Abstractions;

public interface IJwtProvider
{
    string GenerateToken(Guid userId, Guid roleId, DateTime expiresUtc);
    bool TryValidateToken(string token, out Guid userId, out Guid roleId);
}

