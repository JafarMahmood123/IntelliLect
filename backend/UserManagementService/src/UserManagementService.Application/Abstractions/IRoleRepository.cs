using UserManagementService.Domain.Entities;

namespace UserManagementService.Application.Abstractions;

public interface IRoleRepository
{
    Task<List<Role>> GetSelfRegistrationRolesAsync(CancellationToken ct = default);
}