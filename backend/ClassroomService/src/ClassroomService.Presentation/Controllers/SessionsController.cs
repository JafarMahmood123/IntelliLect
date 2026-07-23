using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ClassroomService.Presentation.Controllers;

[Authorize]
[Route("api/classrooms/{classroomId:guid}/sessions")]
public class SessionsController : ApiBaseController
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
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> CreateSession(Guid classroomId, [FromBody] CreateSessionRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var session = await _sessionService.CreateSessionAsync(classroomId, request, ct);
        return CreatedAtAction(nameof(GetSessions), new { classroomId }, session);
    }

    [HttpPost("{sessionId:guid}/start")]
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> StartSession(Guid sessionId, CancellationToken ct)
    {
        await _sessionService.StartSessionAsync(sessionId, ct);
        return NoContent();
    }

    /// <summary>
    /// The teacher closes their own live session: students are removed from the room, the
    /// recording is finalized and summary generation is triggered. Idempotent — calling it on an
    /// already-ended session returns 200 with <c>alreadyEnded: true</c> instead of an error, so a
    /// double click or a retry after a dropped response is harmless.
    /// </summary>
    [HttpPost("{sessionId:guid}/end")]
    [Authorize(Roles = "Teacher")]
    [ProducesResponseType(typeof(SessionEndOutcome), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> EndSession(Guid classroomId, Guid sessionId, CancellationToken ct)
    {
        var outcome = await _sessionService.EndSessionAsync(classroomId, sessionId, UserId, ct);
        return Ok(outcome);
    }
}
