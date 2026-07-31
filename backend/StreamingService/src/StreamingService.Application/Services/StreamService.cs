using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamingService.Application.Abstractions;
using StreamingService.Application.DTOs;
using StreamingService.Domain.Entities;
using StreamingService.Domain.Enums;

namespace StreamingService.Application.Services;

public sealed class StreamService : IStreamService
{
    private readonly IStreamRepository _streamRepository;
    private readonly IParticipantRepository _participantRepository;
    private readonly IStreamHubContext _hubContext;
    private readonly IMediaProvider _mediaProvider;
    private readonly IRoomLifecycleService _roomLifecycle;
    private readonly IRecordingStarter _recordingStarter;
    private readonly IRecordingEgressService _recordingEgress;
    private readonly IStreamSettings _settings;
    private readonly IMediaSettings _mediaSettings;
    private readonly ILogger<StreamService> _logger;

    public StreamService(
        IStreamRepository streamRepository,
        IParticipantRepository participantRepository,
        IStreamHubContext hubContext,
        IMediaProvider mediaProvider,
        IRoomLifecycleService roomLifecycle,
        IRecordingStarter recordingStarter,
        IRecordingEgressService recordingEgress,
        IStreamSettings settings,
        IMediaSettings mediaSettings,
        ILogger<StreamService> logger)
    {
        _streamRepository = streamRepository;
        _participantRepository = participantRepository;
        _hubContext = hubContext;
        _mediaProvider = mediaProvider;
        _roomLifecycle = roomLifecycle;
        _recordingStarter = recordingStarter;
        _recordingEgress = recordingEgress;
        _settings = settings;
        _mediaSettings = mediaSettings;
        _logger = logger;
    }

    // The client's media quality/reconnection settings travel with the join token so the browser can
    // construct its Room with them (several are frozen at connect time). Server-owned rather than
    // frontend config — see IMediaSettings.
    private static MediaSettingsResponse ToMediaResponse(IMediaSettings s) => new(
        s.AdaptiveStream,
        s.Dynacast,
        s.Simulcast,
        s.VideoCodec,
        s.AudioPreset,
        s.Dtx,
        s.Red,
        s.StopMicTrackOnMute,
        s.VideoWidth,
        s.VideoHeight,
        s.VideoFramerate,
        s.ScreenShareWidth,
        s.ScreenShareHeight,
        s.ScreenShareFramerate,
        s.ScreenShareMaxBitrate,
        s.MaxRetries,
        s.PeerConnectionTimeoutMs,
        s.WebsocketTimeoutMs);

    public async Task<StreamResponse> GetStreamBySessionIdAsync(Guid sessionId, Guid userId, string role, string userName, CancellationToken ct)
    {
        var stream = await _streamRepository.GetBySessionIdAsync(sessionId, true, ct);
        if (stream == null) throw new KeyNotFoundException("Stream not found.");

        // No join token once the session is over — for anyone, teacher included. LiveKit re-creates
        // a room on demand for any valid token, so without this an evicted student could simply
        // reload the page and land back in a freshly created room after the session ended.
        if (stream.Status != StreamStatus.Live)
        {
            _logger.LogInformation(
                "Refused a join token for session {SessionId}: the stream is {Status}.",
                sessionId, stream.Status);
            throw new InvalidOperationException("This session has ended.");
        }

        bool isTeacher = role.Equals("Teacher", StringComparison.OrdinalIgnoreCase);
        // The teacher always publishes freely; a student gets exactly the current per-source policy.
        bool canPublishAudio = isTeacher || stream.StudentsCanPublishAudio;
        bool canPublishVideo = isTeacher || stream.StudentsCanPublishVideo;

        var joinToken = _mediaProvider.GenerateJoinToken(
            sessionId, userId, role, userName, canPublishAudio, canPublishVideo);

        return new StreamResponse(
            stream.Id,
            stream.SessionId,
            stream.Status.ToString(),
            stream.Participants.Count,
            stream.StartedAtUtc,
            joinToken,
            _settings.LiveKitHost,
            (int)stream.ParticipationMode,
            stream.StudentsCanPublishAudio,
            stream.StudentsCanPublishVideo,
            stream.RecordingState.ToString(),
            ToMediaResponse(_mediaSettings));
    }

    public async Task<StudentPublishPolicyResponse> UpdateStudentPublishPolicyAsync(
        Guid sessionId,
        Guid teacherId,
        bool canPublishAudio,
        bool canPublishVideo,
        CancellationToken ct)
    {
        var stream = await _streamRepository.GetBySessionIdAsync(sessionId, false, ct);
        if (stream is null) throw new KeyNotFoundException("Stream not found.");
        if (stream.Status != StreamStatus.Live)
            throw new InvalidOperationException("This session has ended.");
        // Only the session's own teacher may change the policy (defence in depth on top of the
        // controller's [Authorize(Roles = "Teacher")]: a different teacher can't touch this room).
        if (stream.TeacherId != teacherId)
            throw new UnauthorizedAccessException("Only the session's teacher can change publish settings.");

        stream.StudentsCanPublishAudio = canPublishAudio;
        stream.StudentsCanPublishVideo = canPublishVideo;
        await _streamRepository.UpdateAsync(stream, ct);
        await _streamRepository.SaveChangesAsync(ct);

        // Enforce on everyone already connected (force-unpublishes any now-forbidden track). The
        // persisted policy above covers anyone who joins after this point via their join token.
        await _roomLifecycle.ApplyStudentPublishPolicyAsync(sessionId, canPublishAudio, canPublishVideo, ct);

        // Tell every client so their UI reflects the new policy immediately.
        await _hubContext.NotifyPublishPolicyChangedAsync(sessionId, canPublishAudio, canPublishVideo);

        return new StudentPublishPolicyResponse(canPublishAudio, canPublishVideo);
    }

    public async Task<RecordingStateResponse> UpdateRecordingStateAsync(
        Guid sessionId,
        Guid teacherId,
        bool enabled,
        CancellationToken ct = default)
    {
        var stream = await _streamRepository.GetBySessionIdAsync(sessionId, false, ct);
        if (stream is null) throw new KeyNotFoundException("Stream not found.");
        if (stream.Status != StreamStatus.Live)
            throw new InvalidOperationException("This session has ended.");
        // Only the session's own teacher may record it (defence in depth on top of the controller's
        // [Authorize(Roles = "Teacher")]: a different teacher can't touch this room).
        if (stream.TeacherId != teacherId)
            throw new UnauthorizedAccessException("Only the session's teacher can change recording.");

        // Stopping is final. Rejected here rather than only in the UI so the rule holds for any
        // caller — and the recording stays one continuous video.
        if (enabled && stream.RecordingState == RecordingState.Ended)
        {
            throw new InvalidOperationException(
                "Recording has already been stopped for this session and cannot be resumed.");
        }

        // Idempotent: asking for the state it is already in changes nothing. Note this deliberately
        // leaves Off alone when asked to stop — turning off something that never started must not
        // burn the session's one chance to record.
        var isRecording = stream.RecordingState == RecordingState.Recording;
        if (enabled == isRecording)
        {
            return new RecordingStateResponse(stream.RecordingState.ToString());
        }

        // Persist the DESIRED state before touching LiveKit, in both directions. The reconcile loop
        // converges on whatever is stored, so a crash after this point self-heals: "stored Recording
        // but nothing running" gets started, "stored Ended but still running" gets stopped. Calling
        // LiveKit first would invert that — a successful start whose write is lost would be torn
        // down by the very loop meant to protect it.
        stream.RecordingState = enabled ? RecordingState.Recording : RecordingState.Ended;
        await _streamRepository.UpdateAsync(stream, ct);
        await _streamRepository.SaveChangesAsync(ct);

        if (enabled)
        {
            // Best-effort: a failure here is repaired by the reconcile loop within one pass, so the
            // teacher is not left holding an error for something that is about to succeed anyway.
            await _recordingStarter.TryStartAsync(stream, ct);
        }
        else
        {
            await TryStopRecordingAsync(stream, ct);
        }

        _logger.LogInformation(
            "Recording for session {SessionId} set to {State} by teacher {TeacherId}.",
            sessionId, stream.RecordingState, teacherId);

        // Tell every client — students need to see that they are being recorded.
        await _hubContext.NotifyRecordingStateChangedAsync(sessionId, stream.RecordingState.ToString());

        return new RecordingStateResponse(stream.RecordingState.ToString());
    }

    /// <summary>
    /// Stops the running egress on a teacher's request. Deliberately does NOT wait for LiveKit to
    /// finalize: that wait exists at session end only because the room is closed immediately after,
    /// destroying the composite source mid-mux. Here the room stays open, so LiveKit finalizes and
    /// uploads on its own and the egress-ended webhook publishes the result as usual.
    /// </summary>
    private async Task TryStopRecordingAsync(LiveStream stream, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stream.EgressId)) return;

        try
        {
            await _recordingEgress.StopRecordingAsync(stream.EgressId, ct);
        }
        catch (Exception ex)
        {
            // Also the path when a start is still in flight and EgressId holds a claim placeholder
            // rather than a real egress id. Either way the state is already stored as Ended, so the
            // reconcile loop stops whatever is actually running on a later pass.
            _logger.LogWarning(
                ex,
                "Could not stop recording for session {SessionId}; reconciliation will stop it.",
                stream.SessionId);
        }
    }

    public async Task JoinStreamAsync(Guid sessionId, Guid userId, CancellationToken ct)
    {
        // Include participants to get an accurate count
        var stream = await _streamRepository.GetBySessionIdAsync(sessionId, true, ct);
        if (stream == null || stream.Status != StreamStatus.Live)
            throw new InvalidOperationException("Stream is not active.");

        var isAlreadyJoined = stream.Participants.Any(p => p.UserId == userId);

        if (!isAlreadyJoined)
        {
            await _participantRepository.AddAsync(new StreamParticipant
            {
                Id = Guid.NewGuid(),
                StreamId = stream.Id,
                UserId = userId,
                JoinedAtUtc = DateTime.UtcNow
            }, ct);

            await _participantRepository.SaveChangesAsync(ct);

            // Increment count for the broadcast
            await _hubContext.NotifyParticipantCountAsync(sessionId, stream.Participants.Count + 1);
        }
    }

    public async Task LeaveStreamAsync(Guid sessionId, Guid userId, CancellationToken ct)
    {
        var stream = await _streamRepository.GetBySessionIdAsync(sessionId, true, ct);
        if (stream == null) return;

        var participant = await _participantRepository.GetBySessionAndUserAsync(sessionId, userId, ct);
        if (participant != null)
        {
            await _participantRepository.DeleteAsync(participant.Id, ct);
            await _participantRepository.SaveChangesAsync(ct);

            // Notify all clients that count decreased
            await _hubContext.NotifyParticipantCountAsync(sessionId, Math.Max(0, stream.Participants.Count - 1));
        }
    }

    public async Task ToggleHandRaiseAsync(Guid sessionId, Guid userId, bool isRaised, CancellationToken ct)
    {
        var participant = await _participantRepository.GetBySessionAndUserAsync(sessionId, userId, ct);
        if (participant == null) throw new InvalidOperationException("Not a participant.");

        participant.IsHandRaised = isRaised;
        await _participantRepository.UpdateAsync(participant, ct);
        await _participantRepository.SaveChangesAsync(ct);

        await _hubContext.NotifyHandRaisedAsync(sessionId, userId, isRaised);
    }
}