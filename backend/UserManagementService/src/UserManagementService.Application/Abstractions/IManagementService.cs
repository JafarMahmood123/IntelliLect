using UserManagementService.Application.Common;
using UserManagementService.Application.DTOs.User;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Application.Abstractions;

public interface IManagementService
{
    Task<UserResponse> GetUserProfileAsync(Guid userId, CancellationToken ct = default);
    Task<PagedResult<UserResponse>> GetPendingUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct = default);
    Task<PagedResult<UserResponse>> GetAllUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct = default);

    Task ChangeUserStatus(Guid userId, UserStatus newStatus, CancellationToken ct = default);
    Task UpdateUserAsync(Guid userId, UpdateUserRequest request, CancellationToken ct = default);
    Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default);
    Task DeactivateUserAsync(Guid userId, CancellationToken ct = default);
    Task DeleteUserAsync(Guid userId, CancellationToken ct = default);
    Task ReactivateUserAsync(Guid userId, CancellationToken ct = default);
}