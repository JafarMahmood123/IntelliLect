using Microsoft.AspNetCore.Mvc;
using UserManagementService.Application.DTOs;

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
        return Ok(new { UserId = userId });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(
        [FromBody] LoginRequest request, 
        CancellationToken cancellationToken)
    {
        var response = await _authService.AuthenticateAsync(request, cancellationToken);
        return Ok(response);
    }
}