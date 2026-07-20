namespace UserManagementService.Application.DTOs.Auth;

/// <summary>
/// Outcome of the first login stage. Either the caller is fully authenticated
/// (<see cref="Tokens"/> is populated), or a second factor is required and no
/// tokens are issued yet (<see cref="RequiresTwoFactor"/> is true).
/// </summary>
public sealed record LoginResult(
    bool RequiresTwoFactor,
    LoginResponse? Tokens,
    string? Email)
{
    public static LoginResult Authenticated(LoginResponse tokens) =>
        new(false, tokens, null);

    public static LoginResult TwoFactorRequired(string email) =>
        new(true, null, email);
}
