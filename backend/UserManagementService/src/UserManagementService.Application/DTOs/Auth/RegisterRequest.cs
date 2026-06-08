namespace UserManagementService.Application.DTOs.Auth;

public record RegisterRequest(
    string UserName,
    string Email,
    string FirstName,
    string LastName,
    Guid RoleId,
    string Password);