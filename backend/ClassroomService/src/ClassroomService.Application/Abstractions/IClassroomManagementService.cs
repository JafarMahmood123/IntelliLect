using ClassroomService.Application.DTOs;
using ClassroomService.Application.DTOs.Classroom;

namespace ClassroomService.Application.Abstractions;

public interface IClassroomManagementService
{
    Task<PagedResult<ClassroomResponse>> GetAllAsync(int page, int pageSize, CancellationToken ct = default);


    Task<ClassroomResponse> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IEnumerable<ClassroomResponse>> GetByTeacherIdAsync(Guid teacherId, CancellationToken ct = default);
    Task<IEnumerable<ClassroomResponse>> GetEnrolledClassroomsAsync(Guid studentId, CancellationToken ct = default);

    Task<Guid> CreateAsync(Guid teacherId, CreateClassroomRequest request, CancellationToken ct = default);
    Task UpdateAsync(Guid classroomId, Guid teacherId, UpdateClassroomRequest request, CancellationToken ct = default);
    Task DeleteAsync(Guid classroomId, Guid teacherId, CancellationToken ct = default);

    // --- Platform-admin (super admin) operations ---
    Task<PagedResult<AdminClassroomResponse>> GetAdminPagedAsync(
        int page, int pageSize, string? search, Guid? teacherId, CancellationToken ct = default);
    Task<AdminClassroomResponse?> GetAdminByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Updates a classroom without an owner check, using optimistic concurrency.</summary>
    /// <exception cref="KeyNotFoundException">The classroom does not exist.</exception>
    Task AdminUpdateAsync(Guid id, UpdateClassroomRequest request, long expectedVersion, CancellationToken ct = default);
}