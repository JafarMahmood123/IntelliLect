using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Classroom;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace ClassroomService.Presentation.Controllers;

/// <summary>
/// Service-to-service endpoints for other backend services. Not proxied by nginx
/// (only <c>/api/classrooms</c> is public), so it is reachable only on the internal
/// docker network. When an internal secret is configured it must be supplied via the
/// <c>X-Internal-Secret</c> header; this mirrors the convention used by the
/// Knowledge/Streaming internal clients.
/// </summary>
[ApiController]
[Route("api/internal/users")]
public sealed class InternalUsersController : ControllerBase
{
    private const string InternalSecretHeader = "X-Internal-Secret";

    private readonly IClassroomManagementService _classroomManagementService;
    private readonly IConfiguration _configuration;

    public InternalUsersController(
        IClassroomManagementService classroomManagementService,
        IConfiguration configuration)
    {
        _classroomManagementService = classroomManagementService;
        _configuration = configuration;
    }

    /// <summary>
    /// Returns the classrooms a user teaches and is enrolled in. Used by
    /// UserManagementService to build the super admin's user-detail view.
    /// </summary>
    [HttpGet("{userId:guid}/classrooms")]
    [ProducesResponseType(typeof(UserClassroomsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUserClassrooms(Guid userId, CancellationToken ct)
    {
        if (!IsInternalSecretValid())
        {
            return Unauthorized();
        }

        var teaching = await _classroomManagementService.GetByTeacherIdAsync(userId, ct);
        var enrolled = await _classroomManagementService.GetEnrolledClassroomsAsync(userId, ct);

        return Ok(new UserClassroomsResponse(teaching, enrolled));
    }

    // The endpoint is protected by network isolation. If an internal secret is also
    // configured, require callers to present it; when it is unset (e.g. local dev), the
    // header check is skipped so nothing breaks.
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

public sealed record UserClassroomsResponse(
    IEnumerable<ClassroomResponse> Teaching,
    IEnumerable<ClassroomResponse> Enrolled);
