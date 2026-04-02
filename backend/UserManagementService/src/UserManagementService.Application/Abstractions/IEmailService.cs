namespace UserManagementService.Application.Abstractions;
public interface IEmailService { Task SendResetCodeAsync(string email, string code); }