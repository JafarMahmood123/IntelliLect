namespace UserManagementService.Application.DTOs.User;

public record UpdateUserRequest(string FirstName, string LastName, string UserName, string? Bio);