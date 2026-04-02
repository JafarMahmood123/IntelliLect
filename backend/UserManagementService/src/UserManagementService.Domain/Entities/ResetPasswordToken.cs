namespace UserManagementService.Domain.Entities;

public sealed class ResetPasswordToken
{
    // Main Properties
    public Guid Id { get; set; }
    public string Token { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public int RequestCount { get; set; } = 0;
    public DateTime? LastRequestedAtUtc { get; set; }

    // Foreign Keys
    public Guid UserId { get; set; }

    // Navigation Properties
    public User User { get; set; } = null!;

    public ResetPasswordToken() { }

    public bool IsExpired => DateTime.UtcNow >= ExpiresAtUtc;

    public void UpdateToken(string newToken)
    {
        // 1. If the last request was more than 24 hours ago, reset the counter
        if (LastRequestedAtUtc.HasValue && LastRequestedAtUtc.Value.AddDays(1) < DateTime.UtcNow)
        {
            RequestCount = 0;
        }

        // 2. Set the new token and metadata
        Token = newToken;
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15);
        LastRequestedAtUtc = DateTime.UtcNow;
        RequestCount++;
    }
}