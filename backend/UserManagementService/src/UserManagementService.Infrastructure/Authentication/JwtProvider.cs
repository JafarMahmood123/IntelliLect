using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using UserManagementService.Application.Abstractions;

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

    public string GenerateToken(Guid userId, Guid roleId, DateTime expiresUtc)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("UserId is required.", nameof(userId));
        if (roleId == Guid.Empty)
            throw new ArgumentException("RoleId is required.", nameof(roleId));
        if (expiresUtc.Kind != DateTimeKind.Utc)
            expiresUtc = expiresUtc.ToUniversalTime();
        if (expiresUtc <= DateTime.UtcNow)
            throw new ArgumentException("Token expiry must be in the future.", nameof(expiresUtc));

        var signingKey = new SymmetricSecurityKey(_signingKey);
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(UserIdClaim, userId.ToString("D")),
            new Claim(RoleIdClaim, roleId.ToString("D")),
        };

        var token = new JwtSecurityToken(
            issuer: string.IsNullOrWhiteSpace(_issuer) ? null : _issuer,
            audience: string.IsNullOrWhiteSpace(_audience) ? null : _audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresUtc,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public bool TryValidateToken(string token, out Guid userId, out Guid roleId)
    {
        userId = Guid.Empty;
        roleId = Guid.Empty;

        if (string.IsNullOrWhiteSpace(token))
            return false;

        var tokenHandler = new JwtSecurityTokenHandler();
        var signingKey = new SymmetricSecurityKey(_signingKey);

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,

            ValidateIssuer = !string.IsNullOrWhiteSpace(_issuer),
            ValidIssuer = string.IsNullOrWhiteSpace(_issuer) ? null : _issuer,

            ValidateAudience = !string.IsNullOrWhiteSpace(_audience),
            ValidAudience = string.IsNullOrWhiteSpace(_audience) ? null : _audience,

            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        };

        try
        {
            var principal = tokenHandler.ValidateToken(token, validationParameters, out _);

            var uidRaw = principal.FindFirst(UserIdClaim)?.Value;
            var ridRaw = principal.FindFirst(RoleIdClaim)?.Value;
            if (!Guid.TryParse(uidRaw, out var parsedUserId))
                return false;
            if (!Guid.TryParse(ridRaw, out var parsedRoleId))
                return false;

            userId = parsedUserId;
            roleId = parsedRoleId;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[]? TryDecodeBase64(string input)
    {
        // Avoid throwing on invalid base64; treat it as raw text instead.
        try
        {
            return Convert.FromBase64String(input);
        }
        catch
        {
            return null;
        }
    }
}

