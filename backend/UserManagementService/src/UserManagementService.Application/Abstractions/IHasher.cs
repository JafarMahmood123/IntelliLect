namespace UserManagementService.Application.Abstractions;

public interface IHasher
{
    string HashPassword(string password);
    bool VerifyPassword(string password, string passwordHash);
}

