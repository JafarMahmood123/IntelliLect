namespace UserManagementService.Application.Abstractions;

/// <summary>Reads the real-time live-stream snapshot from StreamingService.</summary>
public interface IStreamingInternalClient
{
    /// <summary>
    /// One entry per currently-live stream. Throws on failure so the caller can degrade to
    /// stored data and flag the live details as unavailable (alternate path 4أ).
    /// </summary>
    Task<IReadOnlyList<LiveStreamSnapshot>> GetLiveStreamsAsync(CancellationToken ct = default);
}

/// <summary>Reads which sessions currently have a running AI-assistant pipeline.</summary>
public interface ILiveAssistantInternalClient
{
    /// <summary>Session ids with an active assistant. Throws on failure (see 4أ).</summary>
    Task<IReadOnlyCollection<Guid>> GetActiveSessionIdsAsync(CancellationToken ct = default);
}

/// <summary>A live stream's real-time snapshot (mirrors StreamingService's LiveStreamSnapshot).</summary>
public sealed record LiveStreamSnapshot(
    Guid SessionId,
    Guid ClassroomId,
    Guid TeacherId,
    int ParticipantCount,
    bool IsRecording,
    DateTime? StartedAtUtc);
