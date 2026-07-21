namespace ClassroomService.Domain.Entities;

public enum SessionStatus
{
    Scheduled,
    Live,
    Ended,

    /// <summary>
    /// The super admin is deleting this session and its outputs. It is hidden from teacher/student
    /// session lists while its recording/summary/transcript are purged; if a purge step fails the
    /// session stays in this state so the deletion can be re-run and resume (alternate path 6ب).
    /// Appended last so the existing stored int values (Scheduled=0, Live=1, Ended=2) are unchanged.
    /// </summary>
    PendingDeletion
}