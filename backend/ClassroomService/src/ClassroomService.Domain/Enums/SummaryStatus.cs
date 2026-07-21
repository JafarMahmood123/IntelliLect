namespace ClassroomService.Domain.Enums;

/// <summary>Lifecycle of a session summary (S-4). A row starts <see cref="Generating"/> and
/// transitions to <see cref="Available"/> or <see cref="Failed"/> when KnowledgeService reports the
/// summary pipeline finished (the <c>SessionSummaryReadyMessage</c>). Mirrors
/// <see cref="RecordingStatus"/>.</summary>
public enum SummaryStatus
{
    Generating = 0,
    Available = 1,
    Failed = 2,

    /// <summary>
    /// The super admin is deleting this summary. Hidden from teacher/student listings while its files
    /// are removed; the row stays in this state on a failed file delete so the deletion can resume
    /// (alternate path 6ب). Appended last so existing stored int values are unchanged.
    /// </summary>
    PendingDeletion = 3
}
