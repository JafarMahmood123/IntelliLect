namespace ClassroomService.Domain.Enums;

/// <summary>Lifecycle of a session summary (S-4). A row starts <see cref="Generating"/> and
/// transitions to <see cref="Available"/> or <see cref="Failed"/> when KnowledgeService reports the
/// summary pipeline finished (the <c>SessionSummaryReadyMessage</c>). Mirrors
/// <see cref="RecordingStatus"/>.</summary>
public enum SummaryStatus
{
    Generating = 0,
    Available = 1,
    Failed = 2
}
