namespace UserManagementService.Domain.Entities;

/// <summary>
/// A short-lived, single-use email verification code issued during the second stage
/// of the super admin login (the "المصادقة الثنائية" use-case). The plaintext code is
/// never stored; only its hash lives here, alongside an expiry and a failed-attempt
/// counter used to stop brute-force guessing.
/// </summary>
public sealed class TwoFactorChallenge
{
    /// <summary>Maximum number of incorrect code entries before the challenge is burned.</summary>
    public const int MaxAttempts = 5;

    // Main Properties
    public Guid Id { get; set; }
    public string CodeHash { get; set; } = null!;
    public DateTime ExpiresAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    // Foreign Keys
    public Guid UserId { get; set; }

    // Navigation Properties
    public User User { get; set; } = null!;

    public TwoFactorChallenge() { }

    public bool IsExpired => DateTime.UtcNow >= ExpiresAtUtc;

    /// <summary>True once the caller has burned through <see cref="MaxAttempts"/> wrong codes.</summary>
    public bool HasExceededMaxAttempts => AttemptCount >= MaxAttempts;

    /// <summary>
    /// Refresh this challenge with a new code hash, resetting the expiry and the failed-attempt
    /// counter. Used when a new login is started before a previous challenge was consumed.
    /// </summary>
    public void Refresh(string codeHash, DateTime expiresAtUtc)
    {
        CodeHash = codeHash;
        ExpiresAtUtc = expiresAtUtc;
        AttemptCount = 0;
        CreatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Record one incorrect code entry (Alternate path 8ب).</summary>
    public void RegisterFailedAttempt() => AttemptCount++;
}
