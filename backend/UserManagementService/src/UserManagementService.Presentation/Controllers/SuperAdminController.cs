using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;
using UserManagementService.Application.Common.Admins;
using UserManagementService.Application.DTOs.Admin;
using UserManagementService.Application.DTOs.Classroom;
using UserManagementService.Application.DTOs.Knowledge;
using UserManagementService.Application.DTOs.Output;
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
    private readonly IKnowledgeAdminService _knowledgeAdminService;
    private readonly IOutputAdminService _outputAdminService;

    public SuperAdminController(
        ISuperAdminService superAdminService,
        IUserDirectoryService userDirectoryService,
        IUserStatusService userStatusService,
        IClassroomAdminService classroomAdminService,
        ISessionMonitorService sessionMonitorService,
        IKnowledgeAdminService knowledgeAdminService,
        IOutputAdminService outputAdminService)
    {
        _superAdminService = superAdminService;
        _userDirectoryService = userDirectoryService;
        _userStatusService = userStatusService;
        _classroomAdminService = classroomAdminService;
        _sessionMonitorService = sessionMonitorService;
        _knowledgeAdminService = knowledgeAdminService;
        _outputAdminService = outputAdminService;
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

    [HttpPut("classrooms/{id:guid}/teacher")]
    [ProducesResponseType(typeof(ClassroomTeacherChangeSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ChangeClassroomTeacher(Guid id, [FromBody] ChangeClassroomTeacherRequest request, CancellationToken ct)
    {
        // 1أ/4أ -> ArgumentException (400), 3أ -> NotFoundException (404),
        // 3ب/concurrency -> InvalidOperationException (409), all mapped by GlobalExceptionHandler.
        var result = await _classroomAdminService.ChangeTeacherAsync(id, request, ct);
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

    [HttpGet("sessions/{id:guid}/deletion-impact")]
    [ProducesResponseType(typeof(SessionDeletionImpactResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSessionDeletionImpact(Guid id, CancellationToken ct)
    {
        var impact = await _sessionMonitorService.GetDeletionImpactAsync(id, ct);
        return impact is null ? NotFound() : Ok(impact);
    }

    [HttpDelete("sessions/{id:guid}")]
    [ProducesResponseType(typeof(SessionDeletionSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteSession(Guid id, [FromBody] DeleteSessionRequest request, CancellationToken ct)
    {
        var result = await _sessionMonitorService.DeleteSessionAsync(id, request.Reason, ct);
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

    // --- Content & knowledge-base management ---

    [HttpGet("knowledge/files")]
    [ProducesResponseType(typeof(FileListResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetKnowledgeFiles([FromQuery] SearchFilesRequest request, CancellationToken ct)
    {
        var result = await _knowledgeAdminService.GetFilesAsync(request, ct);
        return Ok(result);
    }

    [HttpGet("knowledge/files/{fileId:guid}")]
    [ProducesResponseType(typeof(FileDetailResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetKnowledgeFileDetail(Guid fileId, CancellationToken ct)
    {
        var detail = await _knowledgeAdminService.GetFileDetailAsync(fileId, ct);
        return detail is null ? NotFound() : Ok(detail);
    }

    [HttpGet("knowledge/stats")]
    [ProducesResponseType(typeof(KnowledgeStatsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetKnowledgeStats([FromQuery] Guid? classroomId, CancellationToken ct)
    {
        var stats = await _knowledgeAdminService.GetStatsAsync(classroomId, ct);
        return Ok(stats);
    }

    [HttpPost("knowledge/files/{fileId:guid}/reindex")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReindexFile(Guid fileId, [FromBody] ReindexFileRequest request, CancellationToken ct)
    {
        await _knowledgeAdminService.ReindexFileAsync(fileId, request, ct);
        return Accepted();
    }

    [HttpPost("knowledge/classrooms/{classroomId:guid}/reindex")]
    [ProducesResponseType(typeof(BulkReindexResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReindexClassroom(Guid classroomId, [FromBody] ReindexClassroomRequest request, CancellationToken ct)
    {
        var result = await _knowledgeAdminService.ReindexClassroomAsync(classroomId, request, ct);
        return Ok(result);
    }

    [HttpDelete("knowledge/files/{fileId:guid}")]
    [ProducesResponseType(typeof(FileDeletionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteKnowledgeFile(Guid fileId, [FromBody] DeleteFileAdminRequest request, CancellationToken ct)
    {
        var result = await _knowledgeAdminService.DeleteFileAsync(fileId, request, ct);
        return Ok(result);
    }

    // --- Recordings & summaries management ---

    [HttpGet("outputs")]
    [ProducesResponseType(typeof(OutputListResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOutputs([FromQuery] SearchOutputsRequest request, CancellationToken ct)
    {
        var result = await _outputAdminService.GetOutputsAsync(request, ct);
        return Ok(result);
    }

    [HttpDelete("outputs/recordings/{id:guid}")]
    [ProducesResponseType(typeof(OutputDeletionSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteRecording(Guid id, [FromBody] DeleteOutputRequest request, CancellationToken ct)
    {
        var result = await _outputAdminService.DeleteRecordingAsync(id, request.Reason, ct);
        return Ok(result);
    }

    [HttpDelete("outputs/summaries/{id:guid}")]
    [ProducesResponseType(typeof(OutputDeletionSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteSummary(Guid id, [FromBody] DeleteOutputRequest request, CancellationToken ct)
    {
        var result = await _outputAdminService.DeleteSummaryAsync(id, request.Reason, ct);
        return Ok(result);
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
