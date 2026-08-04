namespace UserManagementService.Application.DTOs.User;

/// <summary>
/// Body for a super admin bulk status change: one action applied to many accounts.
/// <see cref="Action"/> is the same case-insensitive UserStatusAction name the single-account
/// endpoint takes.
/// </summary>
public sealed record BulkChangeUserStatusRequest(
    IReadOnlyCollection<Guid> UserIds,
    string Action);

/// <summary>
/// What happened to ONE account in a bulk request.
///
/// Every requested id gets a row, including the ones that failed — a caller approving 200
/// registrations needs to know which three did not take, and why, not just that "some" failed.
/// </summary>
/// <param name="Succeeded">True for an applied change AND for a no-op (see <paramref name="Status"/>).</param>
/// <param name="Status">The account's status after the request, or null when the id could not be resolved.</param>
/// <param name="Error">Human-readable reason, present only when <paramref name="Succeeded"/> is false.</param>
public sealed record BulkUserStatusItem(
    Guid UserId,
    bool Succeeded,
    string? Status,
    string? Error);

/// <summary>
/// The outcome of a bulk status change.
///
/// Deliberately NOT all-or-nothing: one unknown id must not sink 199 valid approvals. The counts
/// are computed from <see cref="Results"/> so they can never disagree with it.
/// </summary>
public sealed record BulkUserStatusResult(
    int Requested,
    int Succeeded,
    int Failed,
    IReadOnlyList<BulkUserStatusItem> Results)
{
    public static BulkUserStatusResult From(IReadOnlyList<BulkUserStatusItem> results) =>
        new(results.Count, results.Count(r => r.Succeeded), results.Count(r => !r.Succeeded), results);
}
