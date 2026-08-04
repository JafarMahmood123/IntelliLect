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

    /// <summary>
    /// Applies one action to many accounts, for clearing a queue of pending registrations.
    ///
    /// Partial success is the CONTRACT, not a failure mode: every requested id comes back with its
    /// own outcome, and the valid ones are applied regardless of the invalid ones. Callers must
    /// read <see cref="BulkUserStatusResult.Results"/> rather than assuming the whole batch took.
    /// </summary>
    /// <param name="userIds">Deduplicated internally. Empty is rejected; over the cap is rejected.</param>
    /// <param name="action">Case-insensitive UserStatusAction name, applied to every account.</param>
    /// <param name="requestingSuperAdminId">The acting super admin, checked against EACH account.</param>
    Task<BulkUserStatusResult> ChangeStatusBulkAsync(
        IReadOnlyCollection<Guid> userIds,
        string action,
        Guid requestingSuperAdminId,
        CancellationToken ct = default);
}
