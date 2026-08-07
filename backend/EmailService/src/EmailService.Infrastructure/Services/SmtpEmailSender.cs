using EmailService.Application.Abstractions;
using EmailService.Application.Common;
using EmailService.Infrastructure.Configuration;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace EmailService.Infrastructure.Services;

public sealed class SmtpEmailSender : IEmailSender
{
    private readonly EmailSettings _settings;
    private readonly IEmailBodyFactory _emailBodyFactory;
    private readonly Func<ISmtpClient> _clientFactory;

    /// <summary>
    /// The transport client arrives through a factory rather than being constructed inline.
    ///
    /// <c>SmtpClient</c> is a single-use disposable, so a factory is how it would be injected in
    /// any case — but the reason it is injected at all is that this class was, until now, the one
    /// piece of the service with no test behind it: work-plan §7.6 records its coverage gap as
    /// "needs a real SMTP server". It does. The seam lets the tests give it one, in-process, and
    /// exercise the real MailKit conversation — greeting, EHLO, STARTTLS handshake, SASL, DATA —
    /// against a socket instead of asserting on a mock's call list.
    ///
    /// Note what is NOT injected: the security mode. A test that wanted plaintext could ask for it
    /// and the production path would never be the one under test, so the fake server does real TLS
    /// with a real certificate and the test's client trusts it. Requiring encryption stays a
    /// property of this class.
    /// </summary>
    public SmtpEmailSender(
        IOptions<EmailSettings> settings,
        IEmailBodyFactory emailBodyFactory,
        Func<ISmtpClient> clientFactory)
    {
        _settings = settings.Value;
        _emailBodyFactory = emailBodyFactory;
        _clientFactory = clientFactory;
    }

    public async Task SendResetCodeAsync(string email, string code)
    {
        var body = _emailBodyFactory.CreatePasswordResetBody(code);
        await SendHtmlEmailAsync(email, EmailSubjects.PasswordReset, body);
    }

    public async Task SendTwoFactorCodeAsync(string email, string code)
    {
        var body = _emailBodyFactory.CreateTwoFactorCodeBody(code);
        await SendHtmlEmailAsync(email, EmailSubjects.TwoFactorCode, body);
    }

    public async Task SendHtmlEmailAsync(string to, string subject, string htmlBody)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_settings.SenderName, _settings.SenderEmail));
        message.To.Add(new MailboxAddress(string.Empty, to));
        message.Subject = subject;
        message.Body = new TextPart("html") { Text = htmlBody };

        using var client = _clientFactory();

        // Set before connecting: it bounds the connect and the handshake too, which are the parts
        // that hang when a host is unreachable rather than merely slow.
        client.Timeout = (int)TimeSpan.FromSeconds(_settings.TimeoutSeconds).TotalMilliseconds;

        await client.ConnectAsync(_settings.SmtpHost, _settings.SmtpPort, SocketOptionsFor(_settings.Security));
        await client.AuthenticateAsync(_settings.SenderEmail, _settings.AppPassword);
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }

    /// <summary>
    /// Maps our two-value setting onto MailKit's larger enum.
    ///
    /// The mapping exists so that the values MailKit offers which do not guarantee encryption are
    /// unreachable from configuration. <c>StartTls</c> — as opposed to
    /// <c>StartTlsWhenAvailable</c> — makes MailKit fail the connection when the server does not
    /// advertise the extension, instead of continuing in the clear and sending the password
    /// anyway.
    /// </summary>
    private static SecureSocketOptions SocketOptionsFor(SmtpSecurity security)
        => security switch
        {
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.StartTls,
        };
}
