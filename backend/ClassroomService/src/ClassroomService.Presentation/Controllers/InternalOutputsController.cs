using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Output;
using ClassroomService.Application.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ClassroomService.Presentation.Filters;

namespace ClassroomService.Presentation.Controllers;

/// <summary>
/// Service-to-service management of session outputs (recordings + summaries) for UserManagementService's
/// super admin. ClassroomService owns both, so it drives the listing and the deletion. Secret-guarded,
/// off the nginx path.
/// </summary>
[ApiController]
[Route("api/internal/outputs")]
[InternalSecret]
public sealed class InternalOutputsController : ControllerBase
{
    private readonly IOutputAdminService _outputs;

    public InternalOutputsController(IOutputAdminService outputs)
    {
        _outputs = outputs;
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

    /// <summary>
    /// Super admin forces a summary rebuild from any state except PendingDeletion. Less
    /// restrictive than the teacher's regenerate (Failed only) because this is the operator
    /// escape hatch — it must be able to rescue a summary stuck in Generating.
    /// </summary>
    [HttpPost("summaries/{id:guid}/regenerate")]
    [ProducesResponseType(typeof(OutputDeletionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RegenerateSummary(Guid id, CancellationToken ct)
    {
        try { return Ok(await _outputs.RegenerateSummaryAsync(id, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ConflictException) { return Conflict(); }
    }

    [HttpDelete("summaries/{id:guid}")]
    [ProducesResponseType(typeof(OutputDeletionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> DeleteSummary(Guid id, [FromBody] DeleteOutputRequest request, CancellationToken ct)
        => DeleteAsync(() => _outputs.DeleteSummaryAsync(id, request?.Reason ?? string.Empty, ct));

    private async Task<IActionResult> DeleteAsync(Func<Task<OutputDeletionResult>> action)
    {
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
}

public sealed record DeleteOutputRequest(string Reason);
