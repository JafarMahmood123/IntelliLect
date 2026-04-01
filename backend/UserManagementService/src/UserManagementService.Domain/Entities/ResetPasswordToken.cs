namespace UserManagementService.Domain.Entities;

public sealed class ResetPasswordToken
{
    // Main Properties
    public Guid Id { get; set; }
    public string Token { get; set; }
    public DateTime ExpiresAtUtc { get; set; }

    // Foreign Keys
    public Guid UserId { get; set; }

    // Navigation Properties
    public User User { get; set; } = null!;

    public ResetPasswordToken() { }

    public bool IsExpired => DateTime.UtcNow >= ExpiresAtUtc;
}