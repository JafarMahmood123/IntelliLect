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
    private readonly IRecordingEgressService _recordingEgress;
    private readonly ILogger<InternalStreamsController> _logger;

    public InternalStreamsController(
        IStreamRepository streamRepository,
        ILiveAssistantInternalClient liveAssistant,
        IRecordingEgressService recordingEgress,
        ILogger<InternalStreamsController> logger)
    {
        _streamRepository = streamRepository;
        _liveAssistant = liveAssistant;
        _recordingEgress = recordingEgress;
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

        // Start recording the room before the first save so the egress id is persisted in one
        // write. Best-effort — recording is an enhancement, so a failure must NOT fail stream
        // creation. Room name matches LiveKitMediaProvider's convention: room == sessionId.
        await TryStartRecordingAsync(stream, ct);

        await _streamRepository.AddAsync(stream, ct);
        await _streamRepository.SaveChangesAsync(ct);

        // The room is now live: tell the assistant to join. Best-effort — the assistant
        // is an enhancement, so a failure here must NOT fail stream creation. Room name
        // and teacher identity match LiveKitMediaProvider's token conventions:
        // room = sessionId, participant identity = userId (the teacher's id).
        await NotifyAssistantStartedAsync(stream, ct);

        return CreatedAtAction(nameof(InitializeStream), new { id = stream.Id });
    }

    /// <summary>
    /// Live-stream snapshot for the super-admin monitor: one row per currently-live stream with
    /// its participant count and whether a recording egress is running.
    /// </summary>
    [HttpGet("live")]
    public async Task<IActionResult> GetLiveStreams(CancellationToken ct)
    {
        var streams = await _streamRepository.GetLiveStreamsAsync(ct);

        var items = streams.Select(s => new LiveStreamSnapshot(
            s.SessionId,
            s.ClassroomId,
            s.TeacherId,
            s.Participants?.Count ?? 0,
            !string.IsNullOrWhiteSpace(s.EgressId),
            s.StartedAtUtc)).ToList();

        return Ok(new { items });
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

        // Stop the recording so LiveKit finalizes and uploads the MP4. Best-effort — see above.
        // Readiness of the uploaded file is reported later via the egress webhook (R-1).
        await TryStopRecordingAsync(stream, ct);

        // Tell the assistant to tear down. Best-effort — see above.
        await NotifyAssistantEndedAsync(sessionId, ct);

        return NoContent();
    }

    private async Task TryStartRecordingAsync(LiveStream stream, CancellationToken ct)
    {
        try
        {
            var egressId = await _recordingEgress.StartRoomRecordingAsync(stream.SessionId.ToString(), ct);
            if (!string.IsNullOrWhiteSpace(egressId))
            {
                stream.EgressId = egressId;
                _logger.LogInformation(
                    "Recording egress {EgressId} started for session {SessionId}.",
                    egressId, stream.SessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not start recording egress for session {SessionId}; continuing without recording.",
                stream.SessionId);
        }
    }

    private async Task TryStopRecordingAsync(LiveStream stream, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stream.EgressId)) return;

        try
        {
            await _recordingEgress.StopRecordingAsync(stream.EgressId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not stop recording egress {EgressId} for session {SessionId}; continuing.",
                stream.EgressId, stream.SessionId);
        }
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

/// <summary>One live stream's real-time snapshot for the super-admin monitor.</summary>
public record LiveStreamSnapshot(
    Guid SessionId,
    Guid ClassroomId,
    Guid TeacherId,
    int ParticipantCount,
    bool IsRecording,
    DateTime? StartedAtUtc);
