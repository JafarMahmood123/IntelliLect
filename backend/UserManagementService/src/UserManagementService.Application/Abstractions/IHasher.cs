namespace UserManagementService.Application.Abstractions;

public interface IHasher
{
    string Hash(string code);
    bool Verify(string oldCode, string codeHash);
}

