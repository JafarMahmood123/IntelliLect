using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using StreamingService.Application.Abstractions;
using StreamingService.Domain.Entities;
using StreamingService.Domain.Enums;

namespace StreamingService.Presentation.Controllers;

[ApiController]
[Route("api/internal/streams")]
public sealed class InternalStreamsController : ControllerBase
{
    private readonly IStreamRepository _streamRepository;
    private readonly ILiveAssistantInternalClient _liveAssistant;
    private readonly ILogger<InternalStreamsController> _logger;

    public InternalStreamsController(
        IStreamRepository streamRepository,
        ILiveAssistantInternalClient liveAssistant,
        ILogger<InternalStreamsController> logger)
    {
        _streamRepository = streamRepository;
        _liveAssistant = liveAssistant;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> InitializeStream([FromBody] InitializeStreamRequest request, CancellationToken ct)
    {
        var exists = await _streamRepository.ExistsAsync(request.SessionId, ct);
        if (exists) return Ok();

        var stream = new LiveStream
        {
            Id = Guid.NewGuid(),
            SessionId = request.SessionId,
            ClassroomId = request.ClassroomId,
            TeacherId = request.TeacherId,
            Status = StreamStatus.Live,
            StartedAtUtc = DateTime.UtcNow,
            ParticipationMode = request.ParticipationMode,
            StreamKey = Guid.NewGuid().ToString("N")
        };

        await _streamRepository.AddAsync(stream, ct);
        await _streamRepository.SaveChangesAsync(ct);

        // The room is now live: tell the assistant to join. Best-effort — the assistant
        // is an enhancement, so a failure here must NOT fail stream creation. Room name
        // and teacher identity match LiveKitMediaProvider's token conventions:
        // room = sessionId, participant identity = userId (the teacher's id).
        await NotifyAssistantStartedAsync(stream, ct);

        return CreatedAtAction(nameof(InitializeStream), new { id = stream.Id });
    }

    [HttpPost("{sessionId:guid}/end")]
    public async Task<IActionResult> EndStream(Guid sessionId, CancellationToken ct)
    {
        var stream = await _streamRepository.GetBySessionIdAsync(sessionId, false, ct);
        if (stream is null) return NotFound();

        if (stream.Status != StreamStatus.Ended)
        {
            stream.Status = StreamStatus.Ended;
            stream.EndedAtUtc = DateTime.UtcNow;
            await _streamRepository.UpdateAsync(stream, ct);
            await _streamRepository.SaveChangesAsync(ct);
        }

        // Tell the assistant to tear down. Best-effort — see above.
        await NotifyAssistantEndedAsync(sessionId, ct);

        return NoContent();
    }

    private async Task NotifyAssistantStartedAsync(LiveStream stream, CancellationToken ct)
    {
        try
        {
            await _liveAssistant.NotifySessionStartedAsync(
                stream.SessionId,
                stream.ClassroomId,
                roomName: stream.SessionId.ToString(),
                teacherIdentity: stream.TeacherId.ToString(),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not notify LiveAssistant that session {SessionId} started; continuing without the assistant.",
                stream.SessionId);
        }
    }

    private async Task NotifyAssistantEndedAsync(Guid sessionId, CancellationToken ct)
    {
        try
        {
            await _liveAssistant.NotifySessionEndedAsync(sessionId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not notify LiveAssistant that session {SessionId} ended; continuing.",
                sessionId);
        }
    }
}

public record InitializeStreamRequest(Guid SessionId, Guid ClassroomId, Guid TeacherId, StudentParticipationMode ParticipationMode);
