using ClassroomService.Application.DTOs.Classroom;
using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Persistence for the super-admin classroom deletion use-case. Deletion touches many tables
/// (recordings, summaries, files, sessions, memberships, the classroom) and must delete object-
/// storage keys before the metadata rows that point at them, so all of its queries live here rather
/// than being scattered across the per-aggregate repositories. Every fetch/remove is classroom-
/// scoped and idempotent, which is what makes a failed deletion re-runnable (6أ).
/// </summary>
public interface IClassroomDeletionRepository
{
    /// <summary>Impact preview for step 3, or null if the classroom does not exist (5أ). Read-only.</summary>
    Task<ClassroomDeletionImpact?> GetImpactAsync(Guid classroomId, CancellationToken ct = default);

    /// <summary>
    /// Loads the tracked classroom (any status) so the deletion service can flip it to
    /// PendingDeletion and later remove it. Returns null if it does not exist (5أ).
    /// </summary>
    Task<Classroom?> GetTrackedAsync(Guid classroomId, CancellationToken ct = default);

    /// <summary>True if the classroom currently has a Live session (precondition 5ب).</summary>
    Task<bool> HasLiveSessionAsync(Guid classroomId, CancellationToken ct = default);

    /// <summary>All recordings for the classroom, including their S3 keys (may be null until Available).</summary>
    Task<List<SessionRecording>> GetRecordingsAsync(Guid classroomId, CancellationToken ct = default);

    /// <summary>All summaries for the classroom, including their Markdown/PDF S3 keys.</summary>
    Task<List<SessionSummary>> GetSummariesAsync(Guid classroomId, CancellationToken ct = default);

    /// <summary>All files for the classroom, including their S3 keys.</summary>
    Task<List<ClassroomFile>> GetFilesAsync(Guid classroomId, CancellationToken ct = default);

    void RemoveRecording(SessionRecording recording);
    void RemoveSummary(SessionSummary summary);
    void RemoveFile(ClassroomFile file);
    void RemoveClassroom(Classroom classroom);

    /// <summary>Bulk-deletes the classroom's sessions (no S3 objects of their own). Returns the count.</summary>
    Task<int> DeleteSessionsAsync(Guid classroomId, CancellationToken ct = default);

    /// <summary>Bulk-deletes the classroom's memberships. Returns the count.</summary>
    Task<int> DeleteMembershipsAsync(Guid classroomId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
