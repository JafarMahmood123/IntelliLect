using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreamingService.Application.Abstractions;

namespace StreamingService.Presentation.Controllers;

[Authorize]
[Route("api/streams")]
public sealed class StreamsController : ApiBaseController
{
    private readonly IStreamService _streamService;

    public StreamsController(IStreamService streamService)
    {
        _streamService = streamService;
    }

    [HttpGet("{sessionId:guid}")]
    public async Task<IActionResult> GetStream(Guid sessionId, CancellationToken ct)
    {
        var role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "Student";

        var response = await _streamService.GetStreamBySessionIdAsync(sessionId, UserId, role, ct);
        return Ok(response);
    }

    [HttpPost("{sessionId:guid}/join")]
    public async Task<IActionResult> Join(Guid sessionId, CancellationToken ct)
    {
        await _streamService.JoinStreamAsync(sessionId, UserId, ct);
        return NoContent();
    }

    [HttpDelete("{sessionId:guid}/leave")]
    public async Task<IActionResult> Leave(Guid sessionId, CancellationToken ct)
    {
        await _streamService.LeaveStreamAsync(sessionId, UserId, ct);
        return NoContent();
    }

    [HttpPut("{sessionId:guid}/hand-raise")]
    public async Task<IActionResult> ToggleHandRaise(Guid sessionId, [FromQuery] bool isRaised, CancellationToken ct)
    {
        await _streamService.ToggleHandRaiseAsync(sessionId, UserId, isRaised, ct);
        return Ok(new { Message = isRaised ? "Hand raised." : "Hand lowered." });
    }
}