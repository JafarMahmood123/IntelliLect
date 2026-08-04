using UserManagementService.Application.Common;
using UserManagementService.Application.DTOs.User;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Application.Abstractions;

public interface IManagementService
{
    Task<UserResponse> GetUserProfileAsync(Guid userId, CancellationToken ct = default);
    Task<PagedResult<UserResponse>> GetPendingUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct = default);
    Task<PagedResult<UserResponse>> GetAllUsersAsync(Guid? roleId, int page, int pageSize, CancellationToken ct = default);

    Task UpdateUserAsync(Guid userId, UpdateUserRequest request, CancellationToken ct = default);
    Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default);

    // Status transitions deliberately do NOT live here. They are owned by IUserStatusService,
    // which is the single place that validates the transition, blocks self-targeting and revokes
    // refresh tokens. This interface previously carried weaker duplicates of three of them.
}
