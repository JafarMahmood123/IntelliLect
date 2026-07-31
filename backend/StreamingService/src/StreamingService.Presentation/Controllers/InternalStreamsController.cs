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
    private readonly IRoomLifecycleService _roomLifecycle;
    private readonly IStreamHubContext _hubContext;
    private readonly ILogger<InternalStreamsController> _logger;

    public InternalStreamsController(
        IStreamRepository streamRepository,
        ILiveAssistantInternalClient liveAssistant,
        IRecordingEgressService recordingEgress,
        IRoomLifecycleService roomLifecycle,
        IStreamHubContext hubContext,
        ILogger<InternalStreamsController> logger)
    {
        _streamRepository = streamRepository;
        _liveAssistant = liveAssistant;
        _recordingEgress = recordingEgress;
        _roomLifecycle = roomLifecycle;
        _hubContext = hubContext;
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
            // Seed the live toggles from the creation-time mode; the teacher can change them later.
            StudentsCanPublishAudio = request.ParticipationMode.AllowsAudio(),
            StudentsCanPublishVideo = request.ParticipationMode.AllowsVideo(),
            // Same shape: the creation-time choice seeds a state the teacher changes live.
            // Recording is opt-in, so anything but an explicit yes means Off.
            RecordingState = request.RecordingEnabled ? RecordingState.Recording : RecordingState.Off,
            StreamKey = Guid.NewGuid().ToString("N")
        };

        // NOTE: recording is NOT started here, even when it was requested. At this point the LiveKit
        // room does not exist yet (it is created when the first participant joins), so starting
        // egress now fails with "requested room does not exist". The state above records the INTENT;
        // it is acted on when LiveKit fires the `room_started` webhook (see
        // LiveKitRecordingWebhookHandler), with the reconcile loop as the safety net.
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

    /// <summary>
    /// Relays a quiz state change from ClassroomService, which owns quizzes, to the live room.
    /// This service holds no quiz state — it only has the socket, so it forwards and nothing more.
    /// Returns 204 even when no stream exists: a quiz outliving its live session is normal, and
    /// failing here would surface as an error on the teacher's perfectly successful action.
    /// </summary>
    [HttpPost("{sessionId:guid}/quiz-event")]
    public async Task<IActionResult> QuizEvent(
        Guid sessionId, [FromBody] QuizEventRequest request, CancellationToken ct)
    {
        try
        {
            await _hubContext.NotifyQuizChangedAsync(sessionId, request.QuizId, request.State);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not broadcast quiz {QuizId} state {State} to session {SessionId}.",
                request.QuizId, request.State, sessionId);
        }

        return NoContent();
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

        // From here on every step is best-effort and independent: the stream is already Ended, and
        // one unreachable dependency must not stop the others from running.

        // Tell the browsers first — they get a "the session ended" message and leave gracefully,
        // instead of being dropped by the room close below with no explanation.
        await NotifyParticipantsEndedAsync(sessionId);

        // Stop the recording so LiveKit finalizes and uploads the MP4. Must happen BEFORE the room
        // is closed, so the egress shuts down cleanly rather than being cut off with the room.
        // Readiness of the uploaded file is reported later via the egress webhook (R-1).
        await TryStopRecordingAsync(stream, ct);

        // Tell the assistant to tear down (this is what flushes the transcript the summary is
        // generated from). Best-effort — see above.
        await NotifyAssistantEndedAsync(sessionId, ct);

        // Finally, close the room itself. This is the authoritative eviction: anyone still
        // connected — a student who ignored the broadcast, or a tab left open in the background —
        // is disconnected by the media server. Room name == sessionId (LiveKitMediaProvider).
        await TryCloseRoomAsync(sessionId, ct);

        return NoContent();
    }

    private async Task TryStopRecordingAsync(LiveStream stream, CancellationToken ct)
    {
        // Only a recording that is actually running needs stopping. Off means there never was one;
        // Ended means the teacher already stopped it mid-session and LiveKit finalized it long ago
        // — and EgressId is deliberately kept after that (the egress-ended webhook correlates by
        // it), so it cannot be used to tell those cases apart.
        if (stream.RecordingState != RecordingState.Recording) return;

        if (string.IsNullOrWhiteSpace(stream.EgressId)) return;

        try
        {
            await _recordingEgress.StopRecordingAsync(stream.EgressId, ct);

            // StopEgress returns once LiveKit ACCEPTS the stop, not once the MP4 is muxed and
            // uploaded. The caller closes the room straight after this, which destroys the
            // composite source — and MP4 writes its index at the END, so a room closed mid-finalize
            // truncates the file. Bounded (see Egress:FinalizeWaitSeconds) so a stuck egress cannot
            // block session end; on timeout we proceed and the recording may be short.
            await _recordingEgress.WaitForFinalizationAsync(stream.EgressId, ct);
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

    private async Task NotifyParticipantsEndedAsync(Guid sessionId)
    {
        try
        {
            await _hubContext.NotifyStreamStatusChangedAsync(sessionId, StreamStatus.Ended.ToString());
        }
        catch (Exception ex)
        {
            // The room close below still evicts them, so this is not fatal.
            _logger.LogWarning(
                ex,
                "Could not broadcast the end of session {SessionId} to connected clients; continuing.",
                sessionId);
        }
    }

    private async Task TryCloseRoomAsync(Guid sessionId, CancellationToken ct)
    {
        try
        {
            await _roomLifecycle.CloseRoomAsync(sessionId.ToString(), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not close the media room for session {SessionId}; participants may need to leave manually.",
                sessionId);
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

public record QuizEventRequest(Guid QuizId, string State);

public record InitializeStreamRequest(
    Guid SessionId,
    Guid ClassroomId,
    Guid TeacherId,
    StudentParticipationMode ParticipationMode,
    // Whether the teacher asked for this session to be recorded when they created it. Defaults to
    // false so an older caller that omits it gets the opt-in default rather than a surprise
    // recording.
    bool RecordingEnabled = false);

/// <summary>One live stream's real-time snapshot for the super-admin monitor.</summary>
public record LiveStreamSnapshot(
    Guid SessionId,
    Guid ClassroomId,
    Guid TeacherId,
    int ParticipantCount,
    bool IsRecording,
    DateTime? StartedAtUtc);
