using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.File;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ClassroomService.Presentation.Filters;

namespace ClassroomService.Presentation.Controllers;

/// <summary>
/// Service-to-service file administration for UserManagementService's super-admin knowledge-base
/// view. ClassroomService owns the file registry (name/size/classroom), so it drives the list and
/// the delete; indexing status is enriched by the caller from RagService. Secret-guarded,
/// off the nginx path.
/// </summary>
[ApiController]
[Route("api/internal/files")]
[InternalSecret]
public sealed class InternalFilesController : ControllerBase
{
    private readonly IFileAdminService _files;

    public InternalFilesController(IFileAdminService files)
    {
        _files = files;
    }

    [HttpGet]
    [ProducesResponseType(typeof(AdminFilePage), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page,
        [FromQuery] int pageSize,
        [FromQuery] string? search,
        [FromQuery] Guid? classroomId,
        CancellationToken ct)
    {
        var result = await _files.GetFilesAsync(search, classroomId, page, pageSize, ct);
        return Ok(result);
    }

    [HttpPost("by-ids")]
    [ProducesResponseType(typeof(IReadOnlyList<AdminFileRow>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetByIds([FromBody] FileIdsRequest request, CancellationToken ct)
    {
        var result = await _files.GetFilesByIdsAsync(request?.FileIds ?? Array.Empty<Guid>(), ct);
        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(typeof(AdminFileDeletionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, [FromBody] DeleteFileRequest request, CancellationToken ct)
    {
        try
        {
            var result = await _files.DeleteFileAsync(id, request?.Reason ?? string.Empty, ct);
            return Ok(result);
        }
        catch (ArgumentException)
        {
            // 6أ: missing reason.
            return BadRequest();
        }
        catch (KeyNotFoundException)
        {
            // 7أ: file does not exist.
            return NotFound();
        }
    }
}

public sealed record FileIdsRequest(IReadOnlyCollection<Guid> FileIds);
public sealed record DeleteFileRequest(string Reason);
