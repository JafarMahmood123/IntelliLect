using UserManagementService.Application.Common;
using UserManagementService.Application.DTOs.User;

namespace UserManagementService.Application.Abstractions;

/// <summary>
/// Read-only directory over all platform users for the super admin: paged search/filter,
/// and a per-user detail view that includes classroom memberships. Purely query-side —
/// it never mutates system state.
/// </summary>
public interface IUserDirectoryService
{
    Task<PagedResult<UserResponse>> SearchUsersAsync(SearchUsersRequest request, CancellationToken ct = default);
    Task<UserDetailResponse> GetUserDetailAsync(Guid userId, CancellationToken ct = default);
}
