using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UserManagementService.Application.Abstractions;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Presentation.Controllers;

[Authorize(Roles = "Admin")]
[ApiController]
[Route("api/admin")]
public sealed class AdminController : ControllerBase
{
    private readonly IManagementService _managementService;

    public AdminController(IManagementService managementService)
    {
        _managementService = managementService;
    }

    [HttpPut("requests/{id}/status")]
    public async Task<IActionResult> HandleRequest(Guid id, [FromBody] string status, CancellationToken ct)
    {
        // Parse string to Enum (Expect "Active" or "Rejected")
        if (!Enum.TryParse<UserStatus>(status, true, out var newStatus))
            return BadRequest("Invalid status.");

        await _managementService.ChangeUserStatus(id, newStatus, ct);
        return NoContent();
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
        await _managementService.DeactivateUserAsync(id, ct);
        return NoContent();
    }

    [HttpPut("users/{id}/reactivate")]
    public async Task<IActionResult> ReactivateUser(Guid id, CancellationToken ct)
    {
        await _managementService.ReactivateUserAsync(id, ct);
        return NoContent();
    }
}
