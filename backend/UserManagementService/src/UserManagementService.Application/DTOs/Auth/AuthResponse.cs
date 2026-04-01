namespace UserManagementService.Application.DTOs;
public record AuthResponse(string AccessToken, string RefreshToken, Guid UserId);