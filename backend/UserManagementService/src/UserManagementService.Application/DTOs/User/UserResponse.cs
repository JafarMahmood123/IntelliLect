namespace UserManagementService.Application.DTOs.User;

public record UserResponse(
    Guid Id,
    string UserName,
    string Email,
    string FirstName,
    string LastName,
    string RoleName,
    string Status,
    string? Bio,
    DateTime CreatedAtUtc);