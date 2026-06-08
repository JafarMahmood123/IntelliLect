namespace UserManagementService.Application.Common.Admins;

public sealed record AdminQueryResult(
    Guid Id,
    string UserName,
    string Email,
    string FirstName,
    string LastName,
    string RoleName,
    string Status,
    string? Bio,
    DateTime CreatedAtUtc,
    Guid Version);
