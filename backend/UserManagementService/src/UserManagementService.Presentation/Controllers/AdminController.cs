using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common.Users;
using UserManagementService.Application.DTOs.User;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Presentation.Controllers;

[Authorize(Roles = "Admin")]
[ApiController]
[Route("api/admin")]
public sealed class AdminController : ControllerBase
{
    private readonly IManagementService _managementService;
    private readonly IUserStatusService _userStatusService;

    public AdminController(
        IManagementService managementService,
        IUserStatusService userStatusService)
    {
        _managementService = managementService;
        _userStatusService = userStatusService;
    }

    /// <summary>
    /// Approves or rejects one pending registration.
    ///
    /// The wire contract is unchanged ("Active" / "Rejected"), but the work is now done by
    /// <see cref="IUserStatusService"/> — the same code the super-admin route runs. The previous
    /// implementation validated no transition, let an admin act on their own account, and did not
    /// revoke refresh tokens on rejection, so a rejected user kept renewing their session.
    /// </summary>
    [HttpPut("requests/{id}/status")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> HandleRequest(Guid id, [FromBody] string status, CancellationToken ct)
    {
        // Expects "Active" or "Rejected" — the STATUS the caller wants, mapped to the action that
        // produces it. Kept as-is so the existing client is unaffected.
        if (!Enum.TryParse<UserStatus>(status, true, out var newStatus))
            return BadRequest("Invalid status.");

        var action = newStatus switch
        {
            UserStatus.Active => UserStatusAction.Accept,
            UserStatus.Rejected => UserStatusAction.Reject,
            _ => (UserStatusAction?)null,
        };

        if (action is null)
            return BadRequest("Invalid status.");

        await _userStatusService.ChangeStatusAsync(id, action.Value.ToString(), GetUserIdFromClaims(), ct);
        return NoContent();
    }

    /// <summary>
    /// Applies one action to many pending registrations, so a queue can be cleared in one pass.
    ///
    /// Always 200 when the request itself is well-formed: partial success is expected, and the body
    /// reports each account separately. A single unknown id does not sink the rest.
    /// </summary>
    [HttpPut("requests/status")]
    [ProducesResponseType(typeof(BulkUserStatusResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> HandleRequestsBulk(
        [FromBody] BulkChangeUserStatusRequest request,
        CancellationToken ct)
    {
        var result = await _userStatusService.ChangeStatusBulkAsync(
            request.UserIds, request.Action, GetUserIdFromClaims(), ct);

        return Ok(result);
    }

    [HttpGet("requests")]
    public async Task<IActionResult> GetPendingRequests(
        [FromQuery] Guid? roleId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken ct = default)
    {
        var result = await _managementService.GetPendingUsersAsync(roleId, page, pageSize, ct);
        return Ok(result);
    }

    [HttpGet("users")]
    public async Task<IActionResult> GetAllUsers(
        [FromQuery] Guid? roleId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken ct = default)
    {
        var result = await _managementService.GetAllUsersAsync(roleId, page, pageSize, ct);
        return Ok(result);
    }

    [HttpPut("users/{id}/deactivate")]
    public async Task<IActionResult> DeactivateUser(Guid id, CancellationToken ct)
    {
        await _userStatusService.ChangeStatusAsync(
            id, nameof(UserStatusAction.Deactivate), GetUserIdFromClaims(), ct);
        return NoContent();
    }

    [HttpPut("users/{id}/reactivate")]
    public async Task<IActionResult> ReactivateUser(Guid id, CancellationToken ct)
    {
        await _userStatusService.ChangeStatusAsync(
            id, nameof(UserStatusAction.Reactivate), GetUserIdFromClaims(), ct);
        return NoContent();
    }

    private Guid GetUserIdFromClaims()
    {
        var userIdClaim = User.FindFirst("uid")?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            throw new UnauthorizedAccessException("Invalid user claims.");
        }
        return userId;
    }
}
