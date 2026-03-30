namespace UserManagementService.Application.Abstractions;

public interface IManagementService
{
    Task<Guid> CreateUserAsync(
        string userName,
        string email,
        string firstName,
        string lastName,
        Guid roleId,
        string password,
        CancellationToken cancellationToken = default);

    Task DeactivateUserAsync(Guid userId, CancellationToken cancellationToken = default);
    Task ActivateUserAsync(Guid userId, CancellationToken cancellationToken = default);
    Task ChangeUserRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default);
}

