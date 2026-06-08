using UserManagementService.Application.Common.Admins;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Application.Abstractions;

public interface IAdminRepository
{
    Task<(List<User> Items, int TotalCount)> GetAdminsAsync(
        AdminQuerySpecification specification,
        CancellationToken ct = default);

    Task<User?> GetAdminByIdAsync(Guid adminId, CancellationToken ct = default);
    Task<Role?> GetAdminRoleAsync(CancellationToken ct = default);
}
