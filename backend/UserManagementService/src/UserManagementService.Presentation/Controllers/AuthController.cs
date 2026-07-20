using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.DTOs;
using UserManagementService.Application.DTOs.Auth;

namespace UserManagementService.Presentation.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(
    [FromBody] RegisterRequest request,
    CancellationToken cancellationToken)
    {
        var userId = await _authService.RegisterAsync(request, cancellationToken);

        // Return 201 Created with the specific blueprint message
        return StatusCode(StatusCodes.Status201Created, new
        {
            Message = "Registration request submitted. Please wait for Admin approval.",
            UserId = userId
        });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(
    [FromBody] LoginRequest request,
    CancellationToken cancellationToken)
    {
        // The service handles credential + status checks, and decides whether a second
        // factor is required (super admins) or the session can be issued immediately.
        var result = await _authService.LoginAsync(request, cancellationToken);

        // Step 6: a 2FA-required login returns no tokens, only a prompt to enter the code.
        if (result.RequiresTwoFactor)
        {
            return Ok(new TwoFactorRequiredResponse(
                result.Email!,
                "A verification code has been sent to your email. Enter it to complete sign-in."));
        }

        return Ok(result.Tokens);
    }

    [HttpPost("verify-2fa")]
    public async Task<IActionResult> VerifyTwoFactor(
    [FromBody] VerifyTwoFactorRequest request,
    CancellationToken cancellationToken)
    {
        var response = await _authService.VerifyTwoFactorAsync(request, cancellationToken);
        return Ok(response);
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequest request, CancellationToken ct)
    {
        var response = await _authService.RefreshAsync(request, ct);
        return Ok(response);
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] RefreshTokenRequest request, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        await _authService.LogoutAsync(userId, request.RefreshToken, ct);
        return Ok(new { Message = "You have been logged out successfully." });
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

    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request, CancellationToken ct)
    {
        await _authService.ForgotPasswordAsync(request.Email, ct);
        return Ok(new { Message = "If an account exists, a reset token has been generated." });
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken ct)
    {
        await _authService.ResetPasswordAsync(request, ct);
        return Ok(new { Message = "Password has been reset successfully." });
    }

    [HttpGet("registration-roles")]
    [ProducesResponseType(typeof(IReadOnlyList<RegistrationRoleResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRegistrationRoles(CancellationToken cancellationToken)
    {
        var roles = await _authService.GetRegistrationRolesAsync(cancellationToken);
        return Ok(roles);
    }
}