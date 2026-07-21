using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.File;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace ClassroomService.Presentation.Controllers;

/// <summary>
/// Service-to-service file administration for UserManagementService's super-admin knowledge-base
/// view. ClassroomService owns the file registry (name/size/classroom), so it drives the list and
/// the delete; indexing status is enriched by the caller from KnowledgeService. Secret-guarded,
/// off the nginx path.
/// </summary>
[ApiController]
[Route("api/internal/files")]
public sealed class InternalFilesController : ControllerBase
{
    private const string InternalSecretHeader = "X-Internal-Secret";

    private readonly IFileAdminService _files;
    private readonly IConfiguration _configuration;

    public InternalFilesController(IFileAdminService files, IConfiguration configuration)
    {
        _files = files;
        _configuration = configuration;
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
        if (!IsInternalSecretValid()) return Unauthorized();

        var result = await _files.GetFilesAsync(search, classroomId, page, pageSize, ct);
        return Ok(result);
    }

    [HttpPost("by-ids")]
    [ProducesResponseType(typeof(IReadOnlyList<AdminFileRow>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetByIds([FromBody] FileIdsRequest request, CancellationToken ct)
    {
        if (!IsInternalSecretValid()) return Unauthorized();

        var result = await _files.GetFilesByIdsAsync(request?.FileIds ?? Array.Empty<Guid>(), ct);
        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(typeof(AdminFileDeletionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, [FromBody] DeleteFileRequest request, CancellationToken ct)
    {
        if (!IsInternalSecretValid()) return Unauthorized();

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

public sealed record FileIdsRequest(IReadOnlyCollection<Guid> FileIds);
public sealed record DeleteFileRequest(string Reason);
