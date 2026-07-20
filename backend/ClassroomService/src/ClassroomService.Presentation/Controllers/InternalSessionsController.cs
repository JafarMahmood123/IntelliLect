using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Session;
using ClassroomService.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace ClassroomService.Presentation.Controllers;

/// <summary>
/// Service-to-service session monitoring and force-end for UserManagementService's super-admin
/// features. Not proxied by nginx, so reachable only on the internal docker network; guarded by
/// the shared <c>X-Internal-Secret</c> header when one is configured. The 2FA-gated authorization
/// happens in the caller (UserManagementService).
/// </summary>
[ApiController]
[Route("api/internal/sessions")]
public sealed class InternalSessionsController : ControllerBase
{
    private const string InternalSecretHeader = "X-Internal-Secret";

    private readonly ISessionAdminService _sessions;
    private readonly IConfiguration _configuration;

    public InternalSessionsController(ISessionAdminService sessions, IConfiguration configuration)
    {
        _sessions = sessions;
        _configuration = configuration;
    }

    [HttpGet]
    public async Task<IActionResult> GetSessions(
        [FromQuery] int page,
        [FromQuery] int pageSize,
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] Guid? classroomId,
        CancellationToken ct)
    {
        if (!IsInternalSecretValid()) return Unauthorized();

        SessionStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<SessionStatus>(status.Trim(), ignoreCase: true, out var s))
            {
                return BadRequest(new { message = "Invalid session status." });
            }
            parsedStatus = s;
        }

        var normalizedPage = page < 1 ? 1 : page;
        var normalizedPageSize = Math.Clamp(pageSize < 1 ? 20 : pageSize, 1, 100);

        var result = await _sessions.GetSessionsAsync(normalizedPage, normalizedPageSize, search, parsedStatus, classroomId, ct);
        return Ok(result);
    }

    [HttpPost("{id:guid}/force-end")]
    [ProducesResponseType(typeof(ForceEndSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ForceEnd(Guid id, [FromBody] ForceEndRequest request, CancellationToken ct)
    {
        if (!IsInternalSecretValid()) return Unauthorized();

        try
        {
            var result = await _sessions.ForceEndAsync(id, request.Reason, ct);
            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            // Alternate path 6أ.
            return NotFound();
        }
    }

    private bool IsInternalSecretValid()
    {
        var expected = _configuration["Internal:ApiSecret"];
        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }

        return Request.Headers.TryGetValue(InternalSecretHeader, out var provided)
            && string.Equals(provided.ToString(), expected, StringComparison.Ordinal);
    }
}

public sealed record ForceEndRequest(string Reason);
