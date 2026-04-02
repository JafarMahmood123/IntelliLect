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
        // The service now handles the Status check
        var response = await _authService.LoginAsync(request, cancellationToken);
        return Ok(response);
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequest request, CancellationToken ct)
    {
        var response = await _authService.RefreshAsync(request, ct);
        return Ok(response);
    }

    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] string email, CancellationToken ct)
    {
        // Note: For security, usually returns 200 OK even if email doesn't exist
        await _authService.ForgotPasswordAsync(email, ct);
        return Ok(new { Message = "If an account exists, a reset token has been generated." });
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken ct)
    {
        await _authService.ResetPasswordAsync(request, ct);
        return Ok(new { Message = "Password has been reset successfully." });
    }
}