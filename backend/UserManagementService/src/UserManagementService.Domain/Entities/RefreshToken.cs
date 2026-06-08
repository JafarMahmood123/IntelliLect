namespace UserManagementService.Domain.Entities;

public sealed class RefreshToken
{
    // Main Properties
    public Guid Id { get; set; }
    public string Token { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public bool IsRevoked { get; set; }

    // Foreign Keys
    public Guid UserId { get; set; }

    // Navigation Properties
    public User User { get; set; } = null!;

    public RefreshToken() { } 

    public bool IsExpired => DateTime.UtcNow >= ExpiresAtUtc;
    public bool IsActive => !IsRevoked && !IsExpired;
    public void Revoke() => IsRevoked = true;
}