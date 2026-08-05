using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Classroom;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ClassroomService.Presentation.Filters;

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
[InternalSecret]
public sealed class InternalUsersController : ControllerBase
{
    private readonly IClassroomManagementService _classroomManagementService;

    public InternalUsersController(
        IClassroomManagementService classroomManagementService)
    {
        _classroomManagementService = classroomManagementService;
    }

    /// <summary>
    /// Returns the classrooms a user teaches and is enrolled in. Used by
    /// UserManagementService to build the super admin's user-detail view.
    /// </summary>
    [HttpGet("{userId:guid}/classrooms")]
    [ProducesResponseType(typeof(UserClassroomsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUserClassrooms(Guid userId, CancellationToken ct)
    {
        var teaching = await _classroomManagementService.GetByTeacherIdAsync(userId, ct);
        var enrolled = await _classroomManagementService.GetEnrolledClassroomsAsync(userId, ct);

        return Ok(new UserClassroomsResponse(teaching, enrolled));
    }
}

public sealed record UserClassroomsResponse(
    IEnumerable<ClassroomResponse> Teaching,
    IEnumerable<ClassroomResponse> Enrolled);
