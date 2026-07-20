namespace IntelliLect.Contracts.Messages;

public sealed record SendTwoFactorCodeMessage(string Email, string Code);
