using System.Text.Json;
using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Logging;
using StreamingService.Application.Abstractions;

namespace StreamingService.Infrastructure.Services;

/// <summary>
/// Closes LiveKit rooms through the server SDK's <see cref="RoomServiceClient"/>, reusing the same
/// API key/secret as token generation and egress. Deleting the room is what forcibly disconnects
/// every remaining participant when a session ends — a client that ignored (or never received)
/// the "session ended" broadcast is still cut off, and because the room is gone it cannot be
/// silently re-created by a stale join token.
/// </summary>
public sealed class LiveKitRoomLifecycleService : IRoomLifecycleService
{
    // Behind ILiveKitRoomClient rather than the SDK type directly, so the decisions below —
    // which participants are touched, and what happens when a call fails — are testable without
    // a live LiveKit. The transport, and its 5s fail-fast timeout, live in LiveKitRoomClient.
    private readonly ILiveKitRoomClient _client;
    private readonly ILogger<LiveKitRoomLifecycleService> _logger;

    public LiveKitRoomLifecycleService(
        ILiveKitRoomClient client,
        ILogger<LiveKitRoomLifecycleService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task CloseRoomAsync(string roomName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(roomName)) return;

        await _client.DeleteRoomAsync(new DeleteRoomRequest { Room = roomName });

        _logger.LogInformation("Closed LiveKit room {RoomName}; all participants disconnected.", roomName);
    }

    public async Task ApplyStudentPublishPolicyAsync(
        Guid sessionId,
        bool canPublishAudio,
        bool canPublishVideo,
        CancellationToken ct = default)
    {
        var roomName = sessionId.ToString();

        ListParticipantsResponse participants;
        try
        {
            participants = await _client.ListParticipantsAsync(new ListParticipantsRequest { Room = roomName });
        }
        catch (Exception ex)
        {
            // No room yet (nobody connected) or LiveKit unreachable: the persisted policy + the join
            // token still cover anyone who joins later, so this is not fatal.
            _logger.LogWarning(
                ex,
                "Could not list participants for session {SessionId}; skipping live enforcement (token still applies).",
                sessionId);
            return;
        }

        // Precompute the runtime source list once; the same policy applies to every student.
        var sources = PublishSourceMap.StudentRuntimeSources(canPublishAudio, canPublishVideo).ToList();
        int updated = 0;

        foreach (var participant in participants.Participants)
        {
            if (!IsStudent(participant.Metadata)) continue; // never touch the teacher or the AI assistant

            var permission = new ParticipantPermission
            {
                CanSubscribe = true,
                CanPublishData = true,
                CanPublish = sources.Count > 0
            };
            permission.CanPublishSources.AddRange(sources);

            try
            {
                await _client.UpdateParticipantAsync(new UpdateParticipantRequest
                {
                    Room = roomName,
                    Identity = participant.Identity,
                    Permission = permission
                });
                updated++;
            }
            catch (Exception ex)
            {
                // One student failing (e.g. just disconnected) must not stop the rest.
                _logger.LogWarning(
                    ex,
                    "Could not update publish permissions for participant {Identity} in session {SessionId}; continuing.",
                    participant.Identity, sessionId);
            }
        }

        _logger.LogInformation(
            "Applied student publish policy to session {SessionId} (audio={Audio}, video={Video}); {Count} student(s) updated.",
            sessionId, canPublishAudio, canPublishVideo, updated);
    }

    /// <summary>True when a participant's LiveKit metadata declares the "Student" role. Anything
    /// else — the teacher, the AI assistant, or malformed/empty metadata — is treated as non-student
    /// and left alone.</summary>
    private static bool IsStudent(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<ParticipantMetadata>(metadata);
            return parsed is not null
                && string.Equals(parsed.Role, "Student", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
