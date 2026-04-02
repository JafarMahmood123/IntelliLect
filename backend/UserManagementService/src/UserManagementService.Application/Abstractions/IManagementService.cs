using UserManagementService.Application.DTOs.User;

public interface IManagementService
{
    Task UpdateUserAsync(Guid userId, UpdateUserRequest request, CancellationToken ct = default);
    Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default);
    Task DeactivateUserAsync(Guid userId, CancellationToken ct = default);
}