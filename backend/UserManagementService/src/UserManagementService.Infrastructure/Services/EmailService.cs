using MailKit.Net.Smtp;
using MimeKit;
using Microsoft.Extensions.Configuration;
using UserManagementService.Application.Abstractions;

namespace UserManagementService.Infrastructure.Services;

public sealed class EmailService : IEmailService
{
    private readonly IConfiguration _config;
    private readonly IEmailBodyFactory _bodyFactory;
    public EmailService(IConfiguration config, IEmailBodyFactory bodyFactory)
    {
        _config = config;
        _bodyFactory = bodyFactory;
    }

    public async Task SendResetCodeAsync(string email, string code)
    {
        var settings = _config.GetSection("EmailSettings");
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("IntelliLect", settings["SenderEmail"]));
        message.To.Add(new MailboxAddress("", email));
        message.Subject = "Your Password Reset Code";
        var htmlBody = _bodyFactory.CreatePasswordResetBody(code);

        message.Body = new TextPart("html") { Text = htmlBody };
        using var client = new SmtpClient();
        await client.ConnectAsync("smtp.gmail.com", 587, MailKit.Security.SecureSocketOptions.StartTls);
        await client.AuthenticateAsync(settings["SenderEmail"], settings["AppPassword"]);
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }

    public async Task SendHtmlEmailAsync(string to, string subject, string htmlBody)
    {
        var settings = _config.GetSection("EmailSettings");
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("IntelliLect", settings["SenderEmail"]));
        message.To.Add(new MailboxAddress("", to));
        message.Subject = subject;

        message.Body = new TextPart("html") { Text = htmlBody };

        using var client = new SmtpClient();
        await client.ConnectAsync("smtp.gmail.com", 587, MailKit.Security.SecureSocketOptions.StartTls);
        await client.AuthenticateAsync(settings["SenderEmail"], settings["AppPassword"]);
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }
}