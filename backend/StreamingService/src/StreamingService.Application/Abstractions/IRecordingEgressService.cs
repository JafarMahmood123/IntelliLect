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
    /// or poll for the uploaded file — use <see cref="WaitForFinalizationAsync"/> for that.
    /// </summary>
    Task StopRecordingAsync(string egressId, CancellationToken ct = default);

    /// <summary>
    /// Polls until the egress reaches a terminal state, or the configured finalize budget runs
    /// out. Returns <c>true</c> if it settled, <c>false</c> on timeout.
    ///
    /// Exists because <see cref="StopRecordingAsync"/> returns when LiveKit ACCEPTS the stop, not
    /// when the MP4 is written: closing the room in that window destroys the composite source
    /// mid-finalization and truncates the file. Never throws — recording is an enhancement, and a
    /// failure here must not stop a session from ending.
    /// </summary>
    Task<bool> WaitForFinalizationAsync(string egressId, CancellationToken ct = default);

    /// <summary>
    /// Egress ids LiveKit reports as running and still STOPPABLE (starting/active), used to
    /// reconcile persisted state against reality. Empty when recording is disabled.
    ///
    /// Deliberately excludes egresses that are already finalizing: they have been told to stop, so
    /// a reconciling caller asking again would only log a failure. That case is routine now that a
    /// teacher can stop recording mid-session.
    ///
    /// THROWS when LiveKit cannot be reached, rather than returning an empty set: "unknown" must
    /// never be read as "nothing is running", or a reconciling caller would treat every live
    /// recording as missing and start a duplicate for each one.
    /// </summary>
    Task<IReadOnlySet<string>> GetActiveEgressIdsAsync(CancellationToken ct = default);
}
