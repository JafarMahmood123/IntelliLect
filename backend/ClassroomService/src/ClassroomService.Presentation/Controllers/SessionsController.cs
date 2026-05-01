using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Session;
using Microsoft.AspNetCore.Mvc;

namespace ClassroomService.Presentation.Controllers;

[Route("api/classrooms/{classroomId:guid}/sessions")]
public class SessionsController : ControllerBase
{
    private readonly ISessionService _sessionService;

    public SessionsController(ISessionService sessionService)
    {
        _sessionService = sessionService;
    }

    [HttpGet]
    public async Task<IActionResult> GetSessions(Guid classroomId, CancellationToken ct)
    {
        var sessions = await _sessionService.GetSessionsByClassroomAsync(classroomId, ct);
        return Ok(sessions);
    }

    [HttpPost]
    public async Task<IActionResult> CreateSession(Guid classroomId, [FromBody] CreateSessionRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var session = await _sessionService.CreateSessionAsync(classroomId, request, ct);
        return CreatedAtAction(nameof(GetSessions), new { classroomId }, session);
    }
}