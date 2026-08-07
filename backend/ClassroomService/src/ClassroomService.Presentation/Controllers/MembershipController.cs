using ClassroomService.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClassroomService.Presentation.Controllers;

[Authorize]
[Route("api/classrooms/{classroomId:guid}/members")]
public sealed class MembershipController : ApiBaseController
{
    private readonly IMembershipService _membershipService;

    public MembershipController(IMembershipService membershipService)
    {
        _membershipService = membershipService;
    }

    [HttpPost("enroll")]
    [Authorize(Roles = "Student")]
    public async Task<IActionResult> Enroll(Guid classroomId, CancellationToken ct)
    {
        await _membershipService.EnrollStudentAsync(classroomId, UserId, ct);
        return Ok(new { Message = "Successfully enrolled in classroom." });
    }

    [HttpGet]
    public async Task<IActionResult> GetMembers(Guid classroomId, CancellationToken ct)
    {
        var response = await _membershipService.GetClassroomMembersAsync(classroomId, UserId, ct);
        return Ok(response);
    }

    [HttpDelete("{studentId:guid}")]
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> RemoveMember(Guid classroomId, Guid studentId, CancellationToken ct)
    {
        await _membershipService.RemoveStudentAsync(classroomId, UserId, studentId, ct);
        return NoContent();
    }
}