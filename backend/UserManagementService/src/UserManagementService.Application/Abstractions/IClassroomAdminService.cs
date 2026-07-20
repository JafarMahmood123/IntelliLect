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
}
