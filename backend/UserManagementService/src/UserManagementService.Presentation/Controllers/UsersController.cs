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

    // Helper method to extract the User ID from the JWT token claims
    private Guid GetUserIdFromClaims()
    {
        // "uid" is the claim name defined in your JwtProvider
        var userIdClaim = User.FindFirst("uid")?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            throw new UnauthorizedAccessException("Invalid user claims.");
        }
        return userId;
    }
}