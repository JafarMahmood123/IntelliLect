namespace UserManagementService.Application.Common;

/// <summary>Named authorization policies used across the API.</summary>
public static class AuthorizationPolicies
{
    /// <summary>
    /// Requires the caller to be a super admin AND to have completed two-factor
    /// authentication in the current session. Guards the sensitive admin-management
    /// endpoints (use-case "إدارة حسابات مدراء النظام", step 2 / alternate path 2أ).
    /// </summary>
    public const string SuperAdminTwoFactor = "SuperAdminTwoFactor";
}

/// <summary>Claim names/values that mark a session as having cleared two-factor auth.</summary>
public static class TwoFactorClaims
{
    // "amr" = Authentication Methods References (RFC 8176). "mfa" = multi-factor completed.
    public const string ClaimType = "amr";
    public const string CompletedValue = "mfa";
}
