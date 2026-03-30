namespace UserManagementService.Application.DTOs;

public record UserResponse(
    Guid Id,
    string UserName,
    string Email,
    string FirstName,
    string LastName,
    string RoleName,
    bool IsActive,
    DateTime CreatedAtUtc);