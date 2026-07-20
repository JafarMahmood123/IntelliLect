using UserManagementService.Application.Common;
using UserManagementService.Application.DTOs.Classroom;

namespace UserManagementService.Application.Abstractions;

/// <summary>
/// Super admin browsing and management of platform classrooms (use-case
/// "استعراض الفصول الدراسية وإدارتها"). Orchestrates ClassroomService (which owns classroom
/// data) and validates/enriches teacher information locally.
/// </summary>
public interface IClassroomAdminService
{
    Task<PagedResult<ClassroomAdminItem>> GetClassroomsAsync(SearchClassroomsRequest request, CancellationToken ct = default);
    Task<Guid> CreateClassroomAsync(CreateClassroomAdminRequest request, CancellationToken ct = default);
    Task UpdateClassroomAsync(Guid classroomId, UpdateClassroomAdminRequest request, CancellationToken ct = default);

    /// <returns>The deletion impact preview (step 3), or null if the classroom does not exist (5أ).</returns>
    Task<ClassroomDeletionImpactResult?> GetDeletionImpactAsync(Guid classroomId, CancellationToken ct = default);

    /// <exception cref="ArgumentException">The confirmation/reason is missing (4أ).</exception>
    Task<ClassroomDeletionSummary> DeleteClassroomAsync(Guid classroomId, DeleteClassroomAdminRequest request, CancellationToken ct = default);
}
