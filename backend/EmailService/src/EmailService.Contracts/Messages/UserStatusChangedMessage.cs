namespace EmailService.Contracts.Messages;

public sealed record UserStatusChangedMessage(string Email, string FirstName, string Status);
