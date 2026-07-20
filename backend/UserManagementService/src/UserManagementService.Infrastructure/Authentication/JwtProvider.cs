using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;

namespace UserManagementService.Infrastructure.Authentication;

public sealed class JwtProvider : IJwtProvider
{
    private const string UserIdClaim = "uid";
    private const string RoleIdClaim = "roleId";

    private readonly string _issuer;
    private readonly string _audience;
    private readonly byte[] _signingKey;

    public JwtProvider(string secretKey, string issuer = "", string audience = "")
    {
        if (string.IsNullOrWhiteSpace(secretKey))
            throw new ArgumentException("Secret key is required.", nameof(secretKey));

        // Accept either raw text or base64-encoded keys.
        _signingKey = TryDecodeBase64(secretKey) ?? Encoding.UTF8.GetBytes(secretKey);
        if (_signingKey.Length < 32)
            throw new ArgumentException("Secret key must be at least 32 bytes.", nameof(secretKey));

        _issuer = issuer ?? string.Empty;
        _audience = audience ?? string.Empty;
    }

    public string GenerateAccessToken(Guid userId, string roleName, string userName, bool twoFactorCompleted = false)
    {
        var signingKey = new SymmetricSecurityKey(_signingKey);
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new Claim("uid", userId.ToString()),
            new Claim(ClaimTypes.Role, roleName),
            new Claim(ClaimTypes.Name, userName)
        };

        // Mark the token as having cleared two-factor authentication. Downstream
        // authorization can require this "amr" (Authentication Methods References)
        // claim before allowing access to sensitive super admin operations.
        if (twoFactorCompleted)
            claims.Add(new Claim(TwoFactorClaims.ClaimType, TwoFactorClaims.CompletedValue));

        var token = new JwtSecurityToken(
            _issuer,
            _audience,
            claims,
            null,
            DateTime.UtcNow.AddMinutes(15),
            credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static byte[]? TryDecodeBase64(string input)
    {
        try
        {
            return Convert.FromBase64String(input);
        }
        catch
        {
            return null;
        }
    }

    public string GenerateRefreshToken()
    {
        var randomNumber = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomNumber);
        return Convert.ToBase64String(randomNumber);
    }
}

