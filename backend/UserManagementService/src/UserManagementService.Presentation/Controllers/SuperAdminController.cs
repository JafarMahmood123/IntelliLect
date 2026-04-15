using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;
using UserManagementService.Application.Common.Admins;
using UserManagementService.Application.DTOs.Admin;

namespace UserManagementService.Presentation.Controllers;

[Authorize(Roles = "SuperAdmin")]
[ApiController]
[Route("api/super-admin")]
public sealed class SuperAdminController : ControllerBase
{
    private readonly ISuperAdminService _superAdminService;

    public SuperAdminController(ISuperAdminService superAdminService)
    {
        _superAdminService = superAdminService;
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

    [HttpDelete("admins/{id:guid}")]
    public async Task<IActionResult> DeleteAdmin(Guid id, CancellationToken ct)
    {
        await _superAdminService.DeleteAdminAsync(id, ct);
        return NoContent();
    }
}
