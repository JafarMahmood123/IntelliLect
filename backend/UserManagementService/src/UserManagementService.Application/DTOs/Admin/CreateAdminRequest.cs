namespace UserManagementService.Application.DTOs.Admin;

public sealed record CreateAdminRequest(
    string UserName,
    string Email,
    string FirstName,
    string LastName,
    string Password,
    string? Bio);
