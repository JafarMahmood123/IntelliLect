using ClassroomService.Application.DTOs.Classroom;

namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Super-admin classroom deletion with impact preview (this use-case). The impact preview (step 3)
/// is read-only; the deletion (steps 5-6) marks the classroom PendingDeletion, then purges its
/// object-storage artifacts and rows phase by phase, S3-object-before-row, so a failure halts with
/// the classroom left PendingDeletion and the deletion can be re-run to resume (6أ).
/// </summary>
public interface IClassroomDeletionService
{
    /// <returns>The deletion impact, or null if the classroom does not exist (5أ).</returns>
    Task<ClassroomDeletionImpact?> GetImpactAsync(Guid classroomId, CancellationToken ct = default);

    /// <exception cref="System.ArgumentException">The reason/confirmation is missing (4أ).</exception>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">The classroom does not exist (5أ).</exception>
    /// <exception cref="Exceptions.ConflictException">The classroom has a live session (5ب).</exception>
    Task<ClassroomDeletionResult> DeleteAsync(Guid classroomId, string reason, CancellationToken ct = default);
}
