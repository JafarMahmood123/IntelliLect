namespace EmailService.Application.Abstractions;

public interface IEmailSender
{
    Task SendResetCodeAsync(string email, string code);
    Task SendHtmlEmailAsync(string to, string subject, string htmlBody);
}
