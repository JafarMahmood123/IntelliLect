namespace UserManagementService.Application.DTOs.User;

/// <summary>
/// Body for a super admin status change. <see cref="Action"/> is the case-insensitive name
/// of a UserStatusAction (Accept / Reject / Deactivate / Reactivate); it is validated in the
/// service so an unknown value is rejected rather than silently defaulting.
/// </summary>
public sealed record ChangeUserStatusRequest(string Action);
