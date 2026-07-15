namespace StreamingService.Application.Abstractions;

/// <summary>
/// Records a live session by driving LiveKit Room Composite Egress. Implementations start a
/// room recording that LiveKit writes DIRECTLY to S3 (bytes never pass through this service)
/// and stop it so LiveKit finalizes and uploads the file.
///
/// This is CAPTURE only (R-0). Confirmation that the file is ready — and the recording
/// metadata flow — arrives later via the egress webhook (R-1), not from these methods.
/// </summary>
public interface IRecordingEgressService
{
    /// <summary>
    /// Starts a room-composite egress that writes an MP4 to S3 for <paramref name="roomName"/>.
    /// Returns the egress id to persist on the stream, or <c>null</c> when recording is
    /// disabled. Throws if the egress cannot be started — callers treat that as non-fatal to
    /// the session (recording is an enhancement).
    /// </summary>
    Task<string?> StartRoomRecordingAsync(string roomName, CancellationToken ct = default);

    /// <summary>
    /// Requests LiveKit stop/finalize the egress identified by <paramref name="egressId"/> so
    /// it uploads the MP4. Throws on failure — callers treat that as non-fatal. Does NOT wait
    /// or poll for the uploaded file.
    /// </summary>
    Task StopRecordingAsync(string egressId, CancellationToken ct = default);
}
