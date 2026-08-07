using EmailService.Application.Common;
using EmailService.Infrastructure.Configuration;
using EmailService.Infrastructure.Services;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;

namespace EmailService.UnitTests;

/// <summary>
/// The SMTP transport itself — connect, STARTTLS, auth (test-plan N-10).
///
/// The last untested code in this service, and the row that has sat at "not covered — needs a real
/// or fake SMTP server" since the suite was written. Work-plan §7.6 records the same gap from the
/// other side: 72.9% coverage, "everything with logic in it at 100%", and
/// <see cref="SmtpEmailSender"/> excluded because it needs a server.
///
/// It gets one. <see cref="FakeSmtpServer"/> listens on a loopback port and the real MailKit
/// client connects to it, so what runs below is the production send path over a real socket with a
/// real TLS handshake — not a recorded call list.
///
/// Writing it found three defects, all in how the settings were read, all sharing one shape: a
/// value that is wrong or missing produces a running service rather than a stopped one.
///
///   1. <c>appsettings.json</c> shipped <c>"AppPassword": ""</c>, and the sender guarded with
///      <c>?? throw</c>. An empty string is not null. The guard was dead code, and a deployment
///      that forgot the environment variable came up healthy and silently lost every email.
///   2. A malformed <c>SmtpPort</c> silently became 587.
///   3. MailKit's 120-second default timeout was never overridden, so one unreachable host held a
///      consumer for six minutes per message across its retries.
///
/// The settings now bind and validate at startup like every other block in the platform, and the
/// tests below cover both what the transport does when it works and what it refuses to do when the
/// server is not what it claims to be.
/// </summary>
public sealed class SmtpTransportTests
{
    private const string SenderEmail = "sender@intellilect.test";
    private const string AppPassword = "app-password-not-a-real-one";

    /// <summary>
    /// A client that trusts the fake server's self-signed certificate, and nothing else about it.
    ///
    /// This is the only concession the tests make. Everything past the handshake — the STARTTLS
    /// negotiation, the SASL exchange, the DATA transmission — is MailKit's real implementation
    /// talking to a real socket.
    /// </summary>
    private static Func<ISmtpClient> TrustingClient()
        => () => new SmtpClient
        {
            ServerCertificateValidationCallback = (_, _, _, _) => true,
            CheckCertificateRevocation = false,
        };

    private static SmtpEmailSender SenderFor(
        FakeSmtpServer server,
        SmtpSecurity security = SmtpSecurity.StartTls,
        int timeoutSeconds = 30,
        string senderEmail = SenderEmail,
        string appPassword = AppPassword)
        => new(
            Options.Create(new EmailSettings
            {
                SenderName = "IntelliLect",
                SenderEmail = senderEmail,
                AppPassword = appPassword,
                SmtpHost = "127.0.0.1",
                SmtpPort = server.Port,
                Security = security,
                TimeoutSeconds = timeoutSeconds,
            }),
            new StubBodyFactory(),
            TrustingClient());

    private sealed class StubBodyFactory : Application.Abstractions.IEmailBodyFactory
    {
        public string CreatePasswordResetBody(string code) => $"<p>reset {code}</p>";
        public string CreateTwoFactorCodeBody(string code) => $"<p>2fa {code}</p>";
        public string CreateStatusChangedBody(string firstName, string status) => "<p>status</p>";
        public string CreateTeacherChangedBody(string a, string b, bool c) => "<p>teacher</p>";
        public string CreateMembershipChangedBody(string a, string b, bool c) => "<p>membership</p>";
    }

    // --- the happy path, command by command ----------------------------------------------------

    [Fact]
    public async Task The_full_conversation_reaches_the_server_in_order()
    {
        await using var server = FakeSmtpServer.Start();

        await SenderFor(server).SendHtmlEmailAsync("student@example.com", "Subject", "<p>Body</p>");

        var verbs = server.Commands.Select(line => line.Split(' ')[0].ToUpperInvariant()).ToList();

        // EHLO twice is the observable evidence of the upgrade: RFC 3207 requires the client to
        // discard what it learned in the clear and re-issue EHLO inside the tunnel. A client that
        // skipped the second one would be trusting capabilities announced by whoever was on the
        // wire before encryption started.
        Assert.Equal(
            ["EHLO", "STARTTLS", "EHLO", "AUTH", "MAIL", "RCPT", "DATA", "QUIT"],
            verbs);
    }

    [Fact]
    public async Task The_credentials_that_reach_the_server_are_the_configured_ones()
    {
        await using var server = FakeSmtpServer.Start();

        await SenderFor(server).SendHtmlEmailAsync("student@example.com", "Subject", "<p>Body</p>");

        // Decoded from the SASL PLAIN token the server received, so this is what crossed the wire
        // rather than what was passed to a method. The sender doubles as the SMTP username, which
        // is Gmail's arrangement and the reason there is no separate username setting.
        Assert.Equal(SenderEmail, server.AuthUser);
        Assert.Equal(AppPassword, server.AuthPassword);
    }

    [Fact]
    public async Task No_credential_crosses_the_wire_before_the_connection_is_encrypted()
    {
        await using var server = FakeSmtpServer.Start();

        await SenderFor(server).SendHtmlEmailAsync("student@example.com", "Subject", "<p>Body</p>");

        // The point of N-10 naming STARTTLS separately from connect. Everything recorded here is
        // what an observer on the network could read; AUTH must not be among it, and neither must
        // the recipient or the message.
        Assert.Equal(["EHLO", "STARTTLS"], server.PlaintextCommands.Select(l => l.Split(' ')[0]));
        Assert.DoesNotContain(server.PlaintextCommands, line => line.Contains(AppPassword, StringComparison.Ordinal));
        Assert.DoesNotContain(server.PlaintextCommands, line => line.Contains("student@example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_envelope_and_the_message_carry_what_was_asked_for()
    {
        await using var server = FakeSmtpServer.Start();

        await SenderFor(server).SendHtmlEmailAsync(
            "student@example.com", "Your account was approved", "<p>Welcome</p>");

        Assert.Contains(server.Commands, line => line.StartsWith($"MAIL FROM:<{SenderEmail}>", StringComparison.Ordinal));
        Assert.Contains(server.Commands, line => line.StartsWith("RCPT TO:<student@example.com>", StringComparison.Ordinal));

        var message = server.DeliveredMessage;
        Assert.NotNull(message);
        Assert.Contains("Subject: Your account was approved", message);
        Assert.Contains("text/html", message);
        Assert.Contains("Welcome", message);
        // The display name reaches the header rather than being dropped on the floor.
        Assert.Contains($"IntelliLect <{SenderEmail}>", message);
    }

    [Fact]
    public async Task A_reset_code_goes_out_with_its_own_subject_over_the_same_transport()
    {
        // The two typed entry points share SendHtmlEmailAsync, but only through this class — so
        // this is the one place that proves the subject constant survives the whole way to DATA
        // rather than only to the seam the consumer tests stop at.
        await using var server = FakeSmtpServer.Start();

        await SenderFor(server).SendResetCodeAsync("student@example.com", "483920");

        Assert.Contains($"Subject: {EmailSubjects.PasswordReset}", server.DeliveredMessage);
        Assert.Contains("483920", server.DeliveredMessage);
    }

    [Fact]
    public async Task Implicit_TLS_on_connect_needs_no_STARTTLS_at_all()
    {
        // The configuration that could not be expressed before: port 465, encrypted from the first
        // byte. The old sender hard-coded SecureSocketOptions.StartTls, so pointing it at such a
        // server produced a handshake failure no setting could correct.
        await using var server = FakeSmtpServer.Start(tlsOnConnect: true);

        await SenderFor(server, SmtpSecurity.SslOnConnect)
            .SendHtmlEmailAsync("student@example.com", "Subject", "<p>Body</p>");

        Assert.DoesNotContain("STARTTLS", server.Commands);
        Assert.Empty(server.PlaintextCommands);
        Assert.Equal(SenderEmail, server.AuthUser);
    }

    // --- what it refuses to do -----------------------------------------------------------------

    [Fact]
    public async Task A_server_that_will_not_encrypt_gets_no_password()
    {
        // The security property, exercised rather than asserted about the enum. MailKit's
        // StartTlsWhenAvailable would carry on in the clear here and authenticate anyway — which
        // is why SmtpSecurity offers no value that maps to it.
        //
        // The server offers AUTH PLAIN unencrypted on purpose. An earlier version withheld it
        // until the connection was secure, and a mutation swapping StartTls for
        // StartTlsWhenAvailable SURVIVED: the downgraded client failed anyway, because there was
        // no mechanism on offer. The refusal has to be the client's, and this is the only setup
        // that can tell the difference.
        await using var server = FakeSmtpServer.Start(FakeSmtpServer.Behaviour.NoStartTls);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            SenderFor(server).SendHtmlEmailAsync("student@example.com", "Subject", "<p>Body</p>"));

        Assert.True(
            server.OfferedAuthInTheClear,
            "the server did not offer plaintext AUTH, so this test cannot show whose decision the "
            + "refusal was");
        Assert.Null(server.AuthPassword);
        Assert.DoesNotContain(server.Commands, line => line.StartsWith("AUTH", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rejected_credentials_surface_as_a_failure_rather_than_a_silent_no_op()
    {
        // What a wrong or revoked app password looks like. It has to throw: N-08 requires the
        // consumer to fault the message so MassTransit retries and then dead-letters it. A sender
        // that swallowed this would report success for mail that was never accepted.
        await using var server = FakeSmtpServer.Start(FakeSmtpServer.Behaviour.RejectAuth);

        var failure = await Assert.ThrowsAsync<AuthenticationException>(() =>
            SenderFor(server).SendHtmlEmailAsync("student@example.com", "Subject", "<p>Body</p>"));

        Assert.Contains("not accepted", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(server.Commands, line => line.StartsWith("MAIL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_connection_lost_mid_message_is_a_failure_not_a_delivery()
    {
        await using var server = FakeSmtpServer.Start(FakeSmtpServer.Behaviour.DropDuringData);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            SenderFor(server).SendHtmlEmailAsync("student@example.com", "Subject", "<p>Body</p>"));

        Assert.Null(server.DeliveredMessage);
    }

    [Fact]
    public async Task Nothing_is_sent_to_a_port_with_no_server_on_it()
    {
        // Stopped before the send, so the configured port has nothing listening on it — the shape
        // of an SMTP host that is down or a port that was mistyped.
        await using var server = FakeSmtpServer.Start();
        await server.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            SenderFor(server).SendHtmlEmailAsync("student@example.com", "Subject", "<p>Body</p>"));
    }

    [Fact]
    public async Task A_host_that_accepts_and_then_says_nothing_gives_up_on_its_own()
    {
        // The timeout defect, made observable. MailKit's default is 120 seconds and nothing
        // overrode it, so a black-holed SMTP host held a consumer for two minutes per attempt and
        // six per message once the three-attempt retry policy had finished with it. Five consumers
        // share this sender.
        await using var server = FakeSmtpServer.Start(FakeSmtpServer.Behaviour.Silent);

        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<Exception>(() =>
            SenderFor(server, timeoutSeconds: 2)
                .SendHtmlEmailAsync("student@example.com", "Subject", "<p>Body</p>"));

        // Generously bounded: the assertion is that the CONFIGURED timeout is the one in force,
        // not the 120-second default. Anything under a minute distinguishes those two.
        Assert.True(
            DateTime.UtcNow - started < TimeSpan.FromSeconds(30),
            "the configured timeout was not applied — MailKit's 120s default is still in force");
    }

    // --- the settings that reach it ------------------------------------------------------------

    [Fact]
    public void Every_setting_the_transport_uses_is_one_the_options_class_declares()
    {
        // A guard on the seam rather than on the wire. The transport tests above all construct
        // EmailSettings directly, so a property renamed out of the class and re-read from
        // IConfiguration somewhere would leave them green; this asserts the sender takes its
        // configuration as bound options and nothing else.
        var constructor = typeof(SmtpEmailSender).GetConstructors().Single();
        var parameters = constructor.GetParameters().Select(p => p.ParameterType).ToList();

        Assert.Contains(typeof(IOptions<EmailSettings>), parameters);
        Assert.DoesNotContain(
            parameters,
            type => type.FullName?.Contains("IConfiguration", StringComparison.Ordinal) == true);
    }
}
