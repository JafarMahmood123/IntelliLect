namespace ClassroomService.Domain.Enums;

/// <summary>Lifecycle of a session recording. A row starts <see cref="Processing"/> (R-0) and
/// transitions to <see cref="Available"/> or <see cref="Failed"/> when the egress finishes (R-1).</summary>
public enum RecordingStatus
{
    Processing = 0,
    Available = 1,
    Failed = 2,

    /// <summary>
    /// The super admin is deleting this recording. It is hidden from teacher/student listings while
    /// its file is removed from the store; if the file delete fails the row stays in this state so
    /// the deletion can be re-run and resume (alternate path 6ب). Appended last so existing stored
    /// int values are unchanged.
    /// </summary>
    PendingDeletion = 3
}
