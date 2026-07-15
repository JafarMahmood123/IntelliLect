namespace ClassroomService.Application.DTOs.Recording;

/// <summary>
/// Client-facing recording metadata (R-2). Deliberately exposes NO s3_key and NO URL — the
/// object location is internal; clients get a download URL later (R-3), never the key.
/// </summary>
public record RecordingSummaryDto(
    Guid RecordingId,
    Guid SessionId,
    Guid ClassroomId,
    string Status,
    int DurationSeconds,
    long SizeBytes,
    string? ContentType,
    DateTime CreatedAt,
    DateTime? AvailableAt
);
