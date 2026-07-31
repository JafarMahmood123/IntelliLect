using StreamingService.Application.DTOs;

namespace StreamingService.Application.Abstractions;

public interface IStreamService
{
    Task<StreamResponse> GetStreamBySessionIdAsync(Guid sessionId, Guid userId, string role, string userName, CancellationToken ct = default);
    Task JoinStreamAsync(Guid sessionId, Guid userId, CancellationToken ct = default);
    Task LeaveStreamAsync(Guid sessionId, Guid userId, CancellationToken ct = default);
    Task ToggleHandRaiseAsync(Guid sessionId, Guid userId, bool isRaised, CancellationToken ct = default);

    /// <summary>
    /// Teacher-only: change whether students may publish audio/video for this live session. Persists
    /// the new policy, enforces it on already-connected students via the media server, and broadcasts
    /// the change so every client updates in real time. <paramref name="teacherId"/> must be the
    /// session's teacher. Returns the applied policy.
    /// </summary>
    Task<StudentPublishPolicyResponse> UpdateStudentPublishPolicyAsync(
        Guid sessionId,
        Guid teacherId,
        bool canPublishAudio,
        bool canPublishVideo,
        CancellationToken ct = default);

    /// <summary>
    /// Teacher-only: start or stop recording this live session. Persists the desired state, acts on
    /// it best-effort, and broadcasts the change so every client's recording indicator updates in
    /// real time. <paramref name="teacherId"/> must be the session's teacher.
    ///
    /// Stopping is FINAL: a stopped recording cannot be resumed, which is what keeps the archived
    /// session one continuous video instead of fragments needing to be stitched. Asking to start an
    /// already-stopped recording is a conflict; asking for the state it is already in is a no-op.
    /// </summary>
    Task<RecordingStateResponse> UpdateRecordingStateAsync(
        Guid sessionId,
        Guid teacherId,
        bool enabled,
        CancellationToken ct = default);
}