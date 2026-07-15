namespace IntelliLect.Contracts.Messages;

/// <summary>
/// Published by StreamingService when a LiveKit egress finishes (R-1). Carries the outcome of
/// a session recording so ClassroomService can transition its <c>SessionRecording</c> to
/// <c>Available</c> (on success) or <c>Failed</c>. New fields after <see cref="Duration"/> are
/// optional with defaults so the contract stays backward compatible with earlier producers.
/// </summary>
public sealed record SessionRecordingReadyMessage(
    Guid SessionId,
    Guid ClassroomId,
    string S3Key,
    long SizeBytes,
    TimeSpan Duration,
    string EgressId = "",
    string ContentType = "video/mp4",
    bool Succeeded = true,
    string? Error = null);
