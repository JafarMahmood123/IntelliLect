namespace UserManagementService.Application.Common.Users;

/// <summary>
/// The status changes a super admin can apply to a user account
/// (use-case "إدارة حالة حساب مستخدم", step 4).
/// </summary>
public enum UserStatusAction
{
    /// <summary>Approve a pending registration request (Pending → Active).</summary>
    Accept = 1,

    /// <summary>Reject a pending registration request (Pending → Rejected).</summary>
    Reject = 2,

    /// <summary>Deactivate an active account (Active → Deactivated).</summary>
    Deactivate = 3,

    /// <summary>Reactivate a deactivated account (Deactivated → Active).</summary>
    Reactivate = 4
}
