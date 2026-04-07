using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.DTOs.User;

namespace UserManagementService.Presentation.Controllers;

[Authorize]
[ApiController]
[Route("api/users")]
public sealed class UsersController : ControllerBase
{
    private readonly IManagementService _managementService;

    public UsersController(IManagementService managementService)
    {
        _managementService = managementService;
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetMe(CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        var profile = await _managementService.GetUserProfileAsync(userId, ct);
        return Ok(profile);
    }

    [HttpPut("me")]
    public async Task<IActionResult> UpdateMe([FromBody] UpdateUserRequest request, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        await _managementService.UpdateUserAsync(userId, request, ct);
        return NoContent(); // 204 Success
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

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        await _managementService.ChangePasswordAsync(userId, request, ct);
        return Ok(new { Message = "Password changed successfully." });
    }

    [HttpPost("deactivate")]
    public async Task<IActionResult> DeactivateSelf(CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        await _managementService.DeactivateUserAsync(userId, ct);
        return Ok(new { Message = "Account deactivated successfully." });
    }
}