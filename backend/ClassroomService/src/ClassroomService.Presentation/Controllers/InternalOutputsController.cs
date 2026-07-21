using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Output;
using ClassroomService.Application.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace ClassroomService.Presentation.Controllers;

/// <summary>
/// Service-to-service management of session outputs (recordings + summaries) for UserManagementService's
/// super admin. ClassroomService owns both, so it drives the listing and the deletion. Secret-guarded,
/// off the nginx path.
/// </summary>
[ApiController]
[Route("api/internal/outputs")]
public sealed class InternalOutputsController : ControllerBase
{
    private const string InternalSecretHeader = "X-Internal-Secret";

    private readonly IOutputAdminService _outputs;
    private readonly IConfiguration _configuration;

    public InternalOutputsController(IOutputAdminService outputs, IConfiguration configuration)
    {
        _outputs = outputs;
        _configuration = configuration;
    }

    [HttpGet]
    [ProducesResponseType(typeof(AdminOutputPage), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page,
        [FromQuery] int pageSize,
        [FromQuery] string? search,
        [FromQuery] string? type,
        [FromQuery] string? status,
        [FromQuery] Guid? classroomId,
        CancellationToken ct)
    {
        if (!IsInternalSecretValid()) return Unauthorized();

        var result = await _outputs.GetOutputsAsync(search, type, status, classroomId, page, pageSize, ct);
        return Ok(result);
    }

    [HttpDelete("recordings/{id:guid}")]
    [ProducesResponseType(typeof(OutputDeletionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> DeleteRecording(Guid id, [FromBody] DeleteOutputRequest request, CancellationToken ct)
        => DeleteAsync(() => _outputs.DeleteRecordingAsync(id, request?.Reason ?? string.Empty, ct));

    [HttpDelete("summaries/{id:guid}")]
    [ProducesResponseType(typeof(OutputDeletionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> DeleteSummary(Guid id, [FromBody] DeleteOutputRequest request, CancellationToken ct)
        => DeleteAsync(() => _outputs.DeleteSummaryAsync(id, request?.Reason ?? string.Empty, ct));

    private async Task<IActionResult> DeleteAsync(Func<Task<OutputDeletionResult>> action)
    {
        if (!IsInternalSecretValid()) return Unauthorized();

        try
        {
            var result = await action();
            return Ok(result);
        }
        catch (ArgumentException)
        {
            // 4أ: missing reason.
            return BadRequest();
        }
        catch (KeyNotFoundException)
        {
            // 5أ: output does not exist.
            return NotFound();
        }
        catch (ConflictException)
        {
            // 5ب: the output's session is live.
            return Conflict();
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

public sealed record DeleteOutputRequest(string Reason);
