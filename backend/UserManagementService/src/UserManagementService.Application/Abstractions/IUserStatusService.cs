using UserManagementService.Application.DTOs.User;

namespace UserManagementService.Application.Abstractions;

/// <summary>
/// Super admin management of a user account's status: accept/reject pending registrations
/// and deactivate/reactivate existing accounts (use-case "إدارة حالة حساب مستخدم").
/// </summary>
public interface IUserStatusService
{
    /// <param name="action">Case-insensitive UserStatusAction name (Accept/Reject/Deactivate/Reactivate).</param>
    /// <param name="requestingSuperAdminId">The acting super admin, used to block self-targeting.</param>
    /// <returns>The user's profile after the change (unchanged if the account was already in the requested state).</returns>
    Task<UserResponse> ChangeStatusAsync(
        Guid userId,
        string action,
        Guid requestingSuperAdminId,
        CancellationToken ct = default);
}
