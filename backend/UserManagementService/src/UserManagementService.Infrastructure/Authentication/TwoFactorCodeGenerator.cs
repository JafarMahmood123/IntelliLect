using System.Security.Cryptography;
using UserManagementService.Application.Abstractions;

namespace UserManagementService.Infrastructure.Authentication;

/// <summary>
/// Produces the six-digit login verification code. Uses a cryptographically strong RNG
/// (rather than <see cref="System.Random"/>) because the code guards a super admin session.
/// </summary>
public sealed class TwoFactorCodeGenerator : ITwoFactorCodeGenerator
{
    private const int Digits = 6;

    public string Generate()
    {
        // Uniform value in [0, 1_000_000) rendered as a zero-padded six-digit string.
        int value = RandomNumberGenerator.GetInt32(0, 1_000_000);
        return value.ToString("D" + Digits);
    }
}
