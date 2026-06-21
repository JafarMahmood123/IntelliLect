using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Classroom;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClassroomService.Presentation.Controllers;

[Authorize]
[Route("api/classrooms")]
public sealed class ClassroomsController : ApiBaseController
{
    private readonly IClassroomManagementService _classroomService;

    public ClassroomsController(IClassroomManagementService classroomService)
    {
        _classroomService = classroomService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(
         [FromQuery] int page = 1,
         [FromQuery] int pageSize = 10,
         CancellationToken ct = default)
    {
        // page must be at least 1
        var validPage = page < 1 ? 1 : page;
        var response = await _classroomService.GetAllAsync(validPage, pageSize, ct);
        return Ok(response);
    }

    [HttpPost]
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> Create([FromBody] CreateClassroomRequest request, CancellationToken ct)
    {
        var id = await _classroomService.CreateAsync(UserId, request, ct);
        return CreatedAtAction(nameof(GetById), new { id }, new { Id = id });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var response = await _classroomService.GetByIdAsync(id, ct);
        return Ok(response);
    }

    [HttpGet("teacher")]
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> GetMyTaughtClasses(CancellationToken ct)
    {
        var response = await _classroomService.GetByTeacherIdAsync(UserId, ct);
        return Ok(response);
    }

    [HttpGet("enrolled")]
    [Authorize(Roles = "Student")]
    public async Task<IActionResult> GetMyEnrolledClasses(CancellationToken ct)
    {
        var response = await _classroomService.GetEnrolledClassroomsAsync(UserId, ct);
        return Ok(response);
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateClassroomRequest request, CancellationToken ct)
    {
        await _classroomService.UpdateAsync(id, UserId, request, ct);
        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _classroomService.DeleteAsync(id, UserId, ct);
        return NoContent();
    }
}