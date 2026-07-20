namespace ClassroomService.Application.DTOs.Classroom;

/// <summary>
/// Read-only preview of what deleting a classroom will destroy (use-case step 3). Computed without
/// mutating anything, so the super admin can weigh the impact before confirming. <see cref="StorageBytes"/>
/// is the object-storage that will be freed (classroom files + session recordings; summaries carry no
/// recorded size). <see cref="HasLiveSession"/> reflects precondition 5ب — a classroom with a live
/// session cannot be deleted yet.
/// </summary>
public sealed record ClassroomDeletionImpact(
    Guid ClassroomId,
    string Name,
    string Status,
    int SessionCount,
    int MemberCount,
    int FileCount,
    int RecordingCount,
    int SummaryCount,
    long StorageBytes,
    bool HasLiveSession);

/// <summary>
/// Outcome of a completed classroom deletion (step 8): the classroom id and how many of each
/// dependent item were removed. On a resumed run the counts reflect only what THIS pass removed,
/// so a fully-cleaned classroom reports zeros for the phases an earlier pass had already finished.
/// </summary>
public sealed record ClassroomDeletionResult(
    Guid ClassroomId,
    int RecordingsDeleted,
    int SummariesDeleted,
    int FilesDeleted,
    int SessionsDeleted,
    int MembershipsDeleted);
