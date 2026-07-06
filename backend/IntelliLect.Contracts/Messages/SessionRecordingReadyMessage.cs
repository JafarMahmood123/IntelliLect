namespace IntelliLect.Contracts.Messages;

public sealed record SessionRecordingReadyMessage(
    Guid SessionId,
    Guid ClassroomId,
    string S3Key,
    long SizeBytes,
    TimeSpan Duration);