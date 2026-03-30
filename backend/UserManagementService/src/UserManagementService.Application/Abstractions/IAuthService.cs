namespace UserManagementService.Application.Abstractions;

public interface IAuthService
{
    Task<Guid> RegisterAsync(
        string userName,
        string email,
        string firstName,
        string lastName,
        Guid roleId,
        string password,
        CancellationToken cancellationToken = default);

    Task<string> AuthenticateAsync(
        string userNameOrEmail,
        string password,
        CancellationToken cancellationToken = default);
}

