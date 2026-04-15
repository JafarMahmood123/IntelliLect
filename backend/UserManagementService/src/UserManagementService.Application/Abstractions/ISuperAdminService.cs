using UserManagementService.Application.Common;
using UserManagementService.Application.Common.Admins;
using UserManagementService.Application.DTOs.Admin;

namespace UserManagementService.Application.Abstractions;

public interface ISuperAdminService
{
    Task<PagedResult<AdminQueryResult>> GetAdminsAsync(GetAdminsRequest request, CancellationToken ct = default);
    Task<GroupedAdminsResponse> GetGroupedAdminsAsync(GetAdminsRequest request, CancellationToken ct = default);
    Task<PagedResult<AdminQueryResult>> SearchAdminsAsync(SearchAdminsRequest request, CancellationToken ct = default);
    Task<Guid> CreateAdminAsync(CreateAdminRequest request, CancellationToken ct = default);
    Task DeleteAdminAsync(Guid adminId, CancellationToken ct = default);
}
