namespace UserManagementService.Application.DTOs.Auth;

public record VerifyTwoFactorRequest(
    string Email,
    string Code);
