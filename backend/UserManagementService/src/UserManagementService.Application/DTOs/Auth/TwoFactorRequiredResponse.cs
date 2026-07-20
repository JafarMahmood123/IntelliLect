namespace UserManagementService.Application.DTOs.Auth;

/// <summary>
/// Response body for a login that has passed credential checks but still needs an
/// email verification code. Deliberately carries no tokens (main scenario, step 6).
/// </summary>
public sealed record TwoFactorRequiredResponse(
    string Email,
    string Message)
{
    public bool RequiresTwoFactor => true;
}
