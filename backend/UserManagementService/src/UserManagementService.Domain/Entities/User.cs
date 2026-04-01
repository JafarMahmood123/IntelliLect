namespace UserManagementService.Domain.Entities;

public sealed class User
{
    // Main Properties
    public Guid Id { get; set; }
    public string UserName { get; set; }
    public string Email { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public string PasswordHash { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public bool IsActive { get; set; }

    // Foreign Keys
    public Guid RoleId { get; set; }

    // Navigation Properties 
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
    public ResetPasswordToken? ResetPasswordToken { get; set; }
    public Role Role { get; set; }

    public User() { }

    public void UpdateInfo(string firstName, string lastName, string userName)
    {
        FirstName = firstName;
        LastName = lastName;
        UserName = userName;
    }

    public void UpdatePassword(string newHash)
    {
        PasswordHash = newHash;
    }

    public void Deactivate()
    {
        IsActive = false;
    }
}