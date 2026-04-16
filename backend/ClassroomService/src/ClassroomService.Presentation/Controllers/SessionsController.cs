using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClassroomService.Presentation.Controllers;

[Authorize]
[Route("api/classrooms/{classroomId:guid}/sessions")]
public sealed class SessionsController : ApiBaseController
{
    private readonly ISessionService _sessionService;

    public SessionsController(ISessionService sessionService)
    {
        _sessionService = sessionService;
    }

    [HttpPost]
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> Schedule(Guid classroomId, [FromBody] CreateSessionRequest request)
    {
        var id = await _sessionService.ScheduleSessionAsync(UserId, classroomId, request);
        return Ok(new { SessionId = id });
    }

    [HttpPost("{sessionId:guid}/start")]
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> Start(Guid sessionId)
    {
        await _sessionService.StartSessionAsync(UserId, sessionId);
        return NoContent();
    }
}