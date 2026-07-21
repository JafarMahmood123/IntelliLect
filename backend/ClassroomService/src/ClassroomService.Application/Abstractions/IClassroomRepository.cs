using ClassroomService.Application.DTOs.Classroom;
using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Abstractions;

public interface IClassroomRepository : IRepository<Classroom>
{
    Task<Classroom?> GetWithDetailsAsync(Guid id, CancellationToken ct = default);
    Task<List<Classroom>> GetByTeacherIdAsync(Guid teacherId, CancellationToken ct = default);
    Task<List<Classroom>> GetEnrolledClassroomsAsync(Guid studentId, CancellationToken ct = default);

    // Admin listing: projects counts (files/students/sessions) and the concurrency version,
    // with optional free-text search and teacher filter.
    Task<(List<AdminClassroomResponse> Items, int TotalCount)> GetAdminPagedAsync(
        int page, int pageSize, string? search, Guid? teacherId, CancellationToken ct = default);

    Task<AdminClassroomResponse?> GetAdminByIdAsync(Guid id, CancellationToken ct = default);

    /// <returns>false if the classroom does not exist. Throws DbUpdateConcurrencyException on a stale version.</returns>
    Task<bool> UpdateWithConcurrencyAsync(
        Guid id, string name, string description, long expectedVersion, CancellationToken ct = default);

    // --- Teacher reassignment (ownership transfer) ---

    /// <summary>Reads the current teacher and name of a classroom (3أ existence + 4ب no-op check + notification data).</summary>
    /// <returns>null if the classroom does not exist.</returns>
    Task<ClassroomTeacherInfo?> GetTeacherInfoAsync(Guid id, CancellationToken ct = default);

    /// <summary>True if the classroom currently has a live session (3ب guards the transfer).</summary>
    Task<bool> HasLiveSessionAsync(Guid classroomId, CancellationToken ct = default);

    /// <summary>Reassigns a classroom to a new teacher under optimistic concurrency (step 5).</summary>
    /// <returns>false if the classroom does not exist. Throws ConflictException on a stale version.</returns>
    Task<bool> ChangeTeacherWithConcurrencyAsync(
        Guid id, Guid newTeacherId, long expectedVersion, CancellationToken ct = default);

    /// <summary>Resolves classroom names for a set of ids (batch enrichment). Missing ids are omitted.</summary>
    Task<List<(Guid Id, string Name)>> GetNamesByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
}