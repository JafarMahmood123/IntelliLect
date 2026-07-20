using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;
using UserManagementService.Application.Common.Admins;
using UserManagementService.Application.DTOs.Admin;
using UserManagementService.Application.DTOs.Classroom;
using UserManagementService.Application.DTOs.Session;
using UserManagementService.Application.DTOs.User;

namespace UserManagementService.Presentation.Controllers;

[Authorize(Policy = AuthorizationPolicies.SuperAdminTwoFactor)]
[ApiController]
[Route("api/super-admin")]
public sealed class SuperAdminController : ControllerBase
{
    private readonly ISuperAdminService _superAdminService;
    private readonly IUserDirectoryService _userDirectoryService;
    private readonly IUserStatusService _userStatusService;
    private readonly IClassroomAdminService _classroomAdminService;
    private readonly ISessionMonitorService _sessionMonitorService;

    public SuperAdminController(
        ISuperAdminService superAdminService,
        IUserDirectoryService userDirectoryService,
        IUserStatusService userStatusService,
        IClassroomAdminService classroomAdminService,
        ISessionMonitorService sessionMonitorService)
    {
        _superAdminService = superAdminService;
        _userDirectoryService = userDirectoryService;
        _userStatusService = userStatusService;
        _classroomAdminService = classroomAdminService;
        _sessionMonitorService = sessionMonitorService;
    }

    [HttpGet("admins")]
    [ProducesResponseType(typeof(PagedResult<AdminQueryResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(GroupedAdminsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAdmins([FromQuery] GetAdminsRequest request, CancellationToken ct)
    {
        if (string.Equals(request.GroupBy, AdminQuerySpecification.StatusGroupField, StringComparison.OrdinalIgnoreCase))
        {
            var groupedResult = await _superAdminService.GetGroupedAdminsAsync(request, ct);
            return Ok(groupedResult);
        }

        var result = await _superAdminService.GetAdminsAsync(request, ct);
        return Ok(result);
    }

    [HttpGet("admins/search")]
    [ProducesResponseType(typeof(PagedResult<AdminQueryResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchAdmins([FromQuery] SearchAdminsRequest request, CancellationToken ct)
    {
        var result = await _superAdminService.SearchAdminsAsync(request, ct);
        return Ok(result);
    }

    [HttpGet("users")]
    [ProducesResponseType(typeof(PagedResult<UserResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchUsers([FromQuery] SearchUsersRequest request, CancellationToken ct)
    {
        var result = await _userDirectoryService.SearchUsersAsync(request, ct);
        return Ok(result);
    }

    [HttpGet("users/{id:guid}")]
    [ProducesResponseType(typeof(UserDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUserDetail(Guid id, CancellationToken ct)
    {
        var result = await _userDirectoryService.GetUserDetailAsync(id, ct);
        return Ok(result);
    }

    [HttpPut("users/{id:guid}/status")]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ChangeUserStatus(
        Guid id,
        [FromBody] ChangeUserStatusRequest request,
        CancellationToken ct)
    {
        var result = await _userStatusService.ChangeStatusAsync(id, request.Action, GetUserIdFromClaims(), ct);
        return Ok(result);
    }

    [HttpGet("classrooms")]
    [ProducesResponseType(typeof(PagedResult<ClassroomAdminItem>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetClassrooms([FromQuery] SearchClassroomsRequest request, CancellationToken ct)
    {
        var result = await _classroomAdminService.GetClassroomsAsync(request, ct);
        return Ok(result);
    }

    [HttpPost("classrooms")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateClassroom([FromBody] CreateClassroomAdminRequest request, CancellationToken ct)
    {
        var id = await _classroomAdminService.CreateClassroomAsync(request, ct);
        return StatusCode(StatusCodes.Status201Created, new { Message = "Classroom created successfully.", Id = id });
    }

    [HttpPut("classrooms/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateClassroom(Guid id, [FromBody] UpdateClassroomAdminRequest request, CancellationToken ct)
    {
        await _classroomAdminService.UpdateClassroomAsync(id, request, ct);
        return NoContent();
    }

    [HttpGet("classrooms/{id:guid}/deletion-impact")]
    [ProducesResponseType(typeof(ClassroomDeletionImpactResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetClassroomDeletionImpact(Guid id, CancellationToken ct)
    {
        var impact = await _classroomAdminService.GetDeletionImpactAsync(id, ct);
        return impact is null ? NotFound() : Ok(impact);
    }

    [HttpDelete("classrooms/{id:guid}")]
    [ProducesResponseType(typeof(ClassroomDeletionSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteClassroom(Guid id, [FromBody] DeleteClassroomAdminRequest request, CancellationToken ct)
    {
        var result = await _classroomAdminService.DeleteClassroomAsync(id, request, ct);
        return Ok(result);
    }

    [HttpGet("sessions")]
    [ProducesResponseType(typeof(PagedResult<SessionMonitorItem>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSessions([FromQuery] SearchSessionsRequest request, CancellationToken ct)
    {
        var result = await _sessionMonitorService.GetSessionsAsync(request, ct);
        return Ok(result);
    }

    [HttpGet("sessions/live")]
    [ProducesResponseType(typeof(LiveSessionsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLiveSessions(CancellationToken ct)
    {
        var result = await _sessionMonitorService.GetLiveSessionsAsync(ct);
        return Ok(result);
    }

    [HttpPost("sessions/{id:guid}/force-end")]
    [ProducesResponseType(typeof(ForceEndSessionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ForceEndSession(Guid id, [FromBody] ForceEndSessionRequest request, CancellationToken ct)
    {
        var result = await _sessionMonitorService.ForceEndAsync(id, request.Reason, ct);
        return Ok(result);
    }

    [HttpPost("admins")]
    public async Task<IActionResult> CreateAdmin([FromBody] CreateAdminRequest request, CancellationToken ct)
    {
        var adminId = await _superAdminService.CreateAdminAsync(request, ct);

        return StatusCode(StatusCodes.Status201Created, new
        {
            Message = "Admin created successfully.",
            AdminId = adminId
        });
    }

    [HttpPut("admins/{id:guid}/deactivate")]
    public async Task<IActionResult> DeactivateAdmin(Guid id, CancellationToken ct)
    {
        await _superAdminService.DeactivateAdminAsync(id, GetUserIdFromClaims(), ct);
        return NoContent();
    }

    [HttpPut("admins/{id:guid}/reactivate")]
    public async Task<IActionResult> ReactivateAdmin(Guid id, CancellationToken ct)
    {
        await _superAdminService.ReactivateAdminAsync(id, GetUserIdFromClaims(), ct);
        return NoContent();
    }

    private Guid GetUserIdFromClaims()
    {
        var userIdClaim = User.FindFirst("uid")?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            throw new UnauthorizedAccessException("Invalid user claims.");
        }
        return userId;
    }
}
