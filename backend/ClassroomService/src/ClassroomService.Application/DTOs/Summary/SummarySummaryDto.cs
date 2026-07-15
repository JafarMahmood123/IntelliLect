namespace ClassroomService.Application.DTOs.Summary;

/// <summary>
/// Client-facing session-summary metadata (S-4). Deliberately exposes NO s3 keys and NO URL — the
/// object locations are internal; clients get a short-lived download URL on demand, never the key.
/// Mirrors <c>RecordingSummaryDto</c>.
/// </summary>
public record SummarySummaryDto(
    Guid SummaryId,
    Guid SessionId,
    Guid ClassroomId,
    string Status,
    DateTime CreatedAt,
    DateTime? AvailableAt
);
