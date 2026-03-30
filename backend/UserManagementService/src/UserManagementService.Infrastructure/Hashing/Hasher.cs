using System.Security.Cryptography;
using UserManagementService.Application.Abstractions;

namespace UserManagementService.Infrastructure.Hashing;

public sealed class Hasher : IHasher
{
    // PBKDF2 parameters. Adjust iterations higher for stronger hashes if needed.
    private const int Iterations = 100_000;
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;

    // Format: v1:iterations:saltBase64:hashBase64
    private const string Version = "v1";

    public string HashPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password is required.", nameof(password));

        byte[] salt = new byte[SaltSizeBytes];
        RandomNumberGenerator.Fill(salt);

        byte[] hash = DeriveHash(password, salt, Iterations);

        return string.Join(
            ":",
            Version,
            Iterations.ToString(),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    public bool VerifyPassword(string password, string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(passwordHash))
            return false;

        try
        {
            // Expected: v1:iterations:saltBase64:hashBase64
            string[] parts = passwordHash.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4)
                return false;

            if (!string.Equals(parts[0], Version, StringComparison.Ordinal))
                return false;

            if (!int.TryParse(parts[1], out int iterations) || iterations <= 0)
                return false;

            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] expectedHash = Convert.FromBase64String(parts[3]);

            byte[] actualHash = DeriveHash(password, salt, iterations);

            return actualHash.Length == expectedHash.Length &&
                   CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch
        {
            // Any parsing/decoding errors should be treated as verification failure.
            return false;
        }
    }

    private static byte[] DeriveHash(string password, byte[] salt, int iterations)
    {
        // SHA256-based PBKDF2.
        return Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashSizeBytes);
    }
}

