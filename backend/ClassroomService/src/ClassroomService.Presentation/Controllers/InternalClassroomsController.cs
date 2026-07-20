using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Classroom;
using ClassroomService.Application.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace ClassroomService.Presentation.Controllers;

/// <summary>
/// Service-to-service classroom administration for UserManagementService's super-admin
/// features. Not proxied by nginx (only <c>/api/classrooms</c> is public), so it is reachable
/// only on the internal docker network; when an internal secret is configured it must be
/// supplied via the <c>X-Internal-Secret</c> header. Teacher validation and the 2FA-gated
/// authorization happen in the caller (UserManagementService).
/// </summary>
[ApiController]
[Route("api/internal/classrooms")]
public sealed class InternalClassroomsController : ControllerBase
{
    private const string InternalSecretHeader = "X-Internal-Secret";

    private readonly IClassroomManagementService _classrooms;
    private readonly IConfiguration _configuration;

    public InternalClassroomsController(IClassroomManagementService classrooms, IConfiguration configuration)
    {
        _classrooms = classrooms;
        _configuration = configuration;
    }

    [HttpGet]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page,
        [FromQuery] int pageSize,
        [FromQuery] string? search,
        [FromQuery] Guid? teacherId,
        CancellationToken ct)
    {
        if (!IsInternalSecretValid()) return Unauthorized();

        var normalizedPage = page < 1 ? 1 : page;
        var normalizedPageSize = Math.Clamp(pageSize < 1 ? 20 : pageSize, 1, 100);

        var result = await _classrooms.GetAdminPagedAsync(normalizedPage, normalizedPageSize, search, teacherId, ct);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(AdminClassroomResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        if (!IsInternalSecretValid()) return Unauthorized();

        var classroom = await _classrooms.GetAdminByIdAsync(id, ct);
        return classroom is null ? NotFound() : Ok(classroom);
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] InternalCreateClassroomRequest request, CancellationToken ct)
    {
        if (!IsInternalSecretValid()) return Unauthorized();

        var id = await _classrooms.CreateAsync(
            request.TeacherId,
            new CreateClassroomRequest(request.Name, request.Description),
            ct);

        return StatusCode(StatusCodes.Status201Created, new { id });
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, [FromBody] InternalUpdateClassroomRequest request, CancellationToken ct)
    {
        if (!IsInternalSecretValid()) return Unauthorized();

        try
        {
            await _classrooms.AdminUpdateAsync(
                id,
                new UpdateClassroomRequest(request.Name, request.Description),
                request.Version,
                ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            // Alternate path 5ج.
            return NotFound();
        }
        catch (ConflictException)
        {
            // Alternate path 6أ: the classroom changed since it was read.
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

public sealed record InternalCreateClassroomRequest(Guid TeacherId, string Name, string Description);
public sealed record InternalUpdateClassroomRequest(string Name, string Description, long Version);
