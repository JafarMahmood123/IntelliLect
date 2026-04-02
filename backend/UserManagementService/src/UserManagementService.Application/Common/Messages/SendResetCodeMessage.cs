namespace UserManagementService.Application.Common.Messages;

public record SendResetCodeMessage(string Email, string Code);