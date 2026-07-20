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
}
