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

    [HttpGet("requests")]
    public async Task<IActionResult> GetPendingRequests(CancellationToken ct)
    {
        var requests = await _managementService.GetPendingUsersAsync(ct);
        return Ok(requests);
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
}