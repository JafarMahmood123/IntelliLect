namespace UserManagementService.Application.Abstractions;

public interface ITwoFactorCodeGenerator
{
    /// <summary>Generates a fresh six-digit numeric verification code.</summary>
    string Generate();
}
