using EmailService.Application.Abstractions;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Configuration;
using MimeKit;

namespace EmailService.Infrastructure.Services;

public sealed class SmtpEmailSender : IEmailSender
{
    private readonly IConfiguration _configuration;
    private readonly IEmailBodyFactory _emailBodyFactory;

    public SmtpEmailSender(IConfiguration configuration, IEmailBodyFactory emailBodyFactory)
    {
        _configuration = configuration;
        _emailBodyFactory = emailBodyFactory;
    }

    public async Task SendResetCodeAsync(string email, string code)
    {
        var body = _emailBodyFactory.CreatePasswordResetBody(code);
        await SendHtmlEmailAsync(email, "Your Password Reset Code", body);
    }

    public async Task SendHtmlEmailAsync(string to, string subject, string htmlBody)
    {
        var settings = _configuration.GetSection("EmailSettings");
        var senderName = settings["SenderName"] ?? "IntelliLect";
        var senderEmail = settings["SenderEmail"] ?? throw new InvalidOperationException("EmailSettings:SenderEmail is required.");
        var appPassword = settings["AppPassword"] ?? throw new InvalidOperationException("EmailSettings:AppPassword is required.");
        var smtpHost = settings["SmtpHost"] ?? "smtp.gmail.com";
        var smtpPort = int.TryParse(settings["SmtpPort"], out var parsedPort) ? parsedPort : 587;

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(senderName, senderEmail));
        message.To.Add(new MailboxAddress(string.Empty, to));
        message.Subject = subject;
        message.Body = new TextPart("html") { Text = htmlBody };

        using var client = new SmtpClient();
        await client.ConnectAsync(smtpHost, smtpPort, MailKit.Security.SecureSocketOptions.StartTls);
        await client.AuthenticateAsync(senderEmail, appPassword);
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }
}
