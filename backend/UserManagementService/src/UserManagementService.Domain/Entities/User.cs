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
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }

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
    }

    public void Approve()
    {
        Status = UserStatus.Active;
    }

    public void Reject()
    {
        Status = UserStatus.Rejected;
    }

    public void Deactivate()
    {
        Status = UserStatus.Deactivated;
    }

    public void UpdatePassword(string newHash)
    {
        PasswordHash = newHash;
    }

    public void SoftDelete()
    {
        IsDeleted = true;
        DeletedAtUtc = DateTime.UtcNow;
        Status = UserStatus.Deactivated;
    }

    public void Restore(Guid newRoleId)
    {
        IsDeleted = false;
        DeletedAtUtc = null;
        Status = UserStatus.Pending;
        RoleId = newRoleId;
    }

    public void Reactivate()
    {
        if (IsDeleted) throw new InvalidOperationException("Cannot reactivate a deleted user. Restore them via registration instead.");
        Status = UserStatus.Active;
    }
}