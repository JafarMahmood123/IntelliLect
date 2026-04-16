namespace EmailService.Contracts.Messages;

public sealed record SendResetCodeMessage(string Email, string Code);
