using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Session;
using ClassroomService.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ClassroomService.Presentation.Filters;

namespace ClassroomService.Presentation.Controllers;

/// <summary>
/// Service-to-service session monitoring and force-end for UserManagementService's super-admin
/// features. Not proxied by nginx, so reachable only on the internal docker network; guarded by
/// the shared <c>X-Internal-Secret</c> header when one is configured. The 2FA-gated authorization
/// happens in the caller (UserManagementService).
/// </summary>
[ApiController]
[Route("api/internal/sessions")]
[InternalSecret]
public sealed class InternalSessionsController : ControllerBase
{
    private readonly ISessionAdminService _sessions;
    private readonly ISessionDeletionService _deletion;

    public InternalSessionsController(
        ISessionAdminService sessions,
        ISessionDeletionService deletion)
    {
        _sessions = sessions;
        _deletion = deletion;
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

    /// <summary>Step 3: read-only deletion impact preview. 404 if the session does not exist (5أ).</summary>
    [HttpGet("{id:guid}/deletion-impact")]
    [ProducesResponseType(typeof(SessionDeletionImpact), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDeletionImpact(Guid id, CancellationToken ct)
    {
        var impact = await _deletion.GetImpactAsync(id, ct);
        return impact is null ? NotFound() : Ok(impact);
    }

    /// <summary>
    /// Steps 5-6: delete the session and its outputs. Idempotent/resumable — re-issuing a delete
    /// that previously failed part-way continues from where it stopped (6ب).
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(typeof(SessionDeletionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, [FromBody] DeleteSessionRequest request, CancellationToken ct)
    {
        try
        {
            var result = await _deletion.DeleteAsync(id, request?.Reason ?? string.Empty, ct);
            return Ok(result);
        }
        catch (ArgumentException)
        {
            // 4أ: missing reason/confirmation.
            return BadRequest();
        }
        catch (KeyNotFoundException)
        {
            // 5أ: session does not exist.
            return NotFound();
        }
        catch (Application.Exceptions.ConflictException)
        {
            // 5ب: the session is live.
            return Conflict();
        }
    }
}

public sealed record ForceEndRequest(string Reason);
public sealed record DeleteSessionRequest(string Reason);
