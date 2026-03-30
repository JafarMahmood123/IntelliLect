namespace UserManagementService.Application.DTOs;

public record AuthResponse(
    string Token,
    Guid UserId,
    string Email);