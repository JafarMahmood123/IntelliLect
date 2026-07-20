namespace ClassroomService.Domain.Enums;

/// <summary>
/// Lifecycle state of a classroom. A classroom is <see cref="Active"/> for its whole normal life.
/// A super-admin delete flips it to <see cref="PendingDeletion"/> first — which hides it from all
/// teacher/student use — then removes its data and outputs phase by phase. If a phase fails the row
/// stays <see cref="PendingDeletion"/> so the delete can be re-run and resume from where it stopped
/// (use-case alternate path 6أ).
/// </summary>
public enum ClassroomStatus
{
    Active = 0,
    PendingDeletion = 1
}
