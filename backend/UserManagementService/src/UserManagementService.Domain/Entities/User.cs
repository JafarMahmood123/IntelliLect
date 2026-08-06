using UserManagementService.Domain.Policies;

namespace UserManagementService.Domain.Entities;

public sealed class User
{
    public Guid Id { get; set; }
    public string UserName { get; set; }
    public string Email { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public string PasswordHash { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public UserStatus Status { get; private set; } = UserStatus.Pending;
    public string? Bio { get; set; }

    // Whether this account requires email-based two-factor verification at login.
    // Super admins are always challenged regardless of this flag (see AuthService),
    // but the column is kept so 2FA can later be opted into for other roles too.
    public bool TwoFactorEnabled { get; set; }

    // Wrong passwords in a row. Reset by a correct one, and by a lock that has run its course —
    // never allowed to accumulate across unrelated occasions.
    public int FailedLoginCount { get; private set; }

    // When the account starts accepting sign-in attempts again. Null means it never stopped.
    public DateTime? LockoutEndsAtUtc { get; private set; }

    public Guid Version { get; private set; } = Guid.NewGuid();

    // Foreign Keys
    public Guid RoleId { get; set; }

    // Navigation Properties 
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
    public ResetPasswordToken? ResetPasswordToken { get; set; }
    public Role Role { get; set; }

    public User() { }

    public void UpdateInfo(string firstName, string lastName, string userName, string? bio)
    {
        FirstName = firstName;
        LastName = lastName;
        UserName = userName;
        Bio = bio;
        Version = Guid.NewGuid();
    }

    public void Approve()
    {
        Status = UserStatus.Active;
        Version = Guid.NewGuid();
    }

    public void Reject()
    {
        Status = UserStatus.Rejected;
        Version = Guid.NewGuid();
    }

    public void Deactivate()
    {
        Status = UserStatus.Deactivated;
        Version = Guid.NewGuid();
    }

    public void UpdatePassword(string newHash)
    {
        PasswordHash = newHash;
    }

    public void Reactivate()
    {
        Status = UserStatus.Active;
        Version = Guid.NewGuid();
    }

    /// <summary>Whether this account is currently refusing sign-in attempts.</summary>
    public bool IsLockedOut(DateTime nowUtc) => LoginLockout.IsLockedOut(LockoutEndsAtUtc, nowUtc);

    /// <summary>
    /// Record a wrong password, locking the account once the run reaches the limit.
    ///
    /// None of these transitions touch <see cref="Version"/>. It is the optimistic-concurrency
    /// token for edits to the account, and a failed sign-in is not an edit anybody is racing —
    /// rolling it here would make two people mistyping their password at once into a conflict.
    /// </summary>
    public void RegisterFailedLogin(DateTime nowUtc)
    {
        // An attempt made while the lock is already up does not extend it. Otherwise a script
        // that keeps guessing keeps the real owner out indefinitely, and a defence against
        // brute force becomes a way to deny one specific person their account — which is a
        // better attack than the one it was built to stop, because it always succeeds.
        if (IsLockedOut(nowUtc)) return;

        // A lock that has expired closes the run it belonged to. Without this the count only
        // ever grows, so four mistyped passwords last term plus one today is a lockout, and
        // every subsequent mistake is another one.
        if (LockoutEndsAtUtc is not null)
        {
            FailedLoginCount = 0;
            LockoutEndsAtUtc = null;
        }

        FailedLoginCount++;

        if (FailedLoginCount >= LoginLockout.MaxFailedAttempts)
        {
            LockoutEndsAtUtc = nowUtc.Add(LoginLockout.Duration);
        }
    }

    /// <summary>The password was right, so the run of failures is over.</summary>
    public void RegisterSuccessfulLogin()
    {
        FailedLoginCount = 0;
        LockoutEndsAtUtc = null;
    }

    /// <summary>Whether anything about the sign-in history still needs writing back.</summary>
    public bool HasFailedLoginHistory => FailedLoginCount != 0 || LockoutEndsAtUtc is not null;
}
