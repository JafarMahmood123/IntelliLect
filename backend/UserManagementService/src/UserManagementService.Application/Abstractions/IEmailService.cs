namespace UserManagementService.Application.Abstractions;

public interface IEmailService
{
    Task SendResetCodeAsync(string email, string code);
    Task SendHtmlEmailAsync(string to, string subject, string htmlBody); // Add this
}