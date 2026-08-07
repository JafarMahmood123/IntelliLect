using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace EmailService.UnitTests;

/// <summary>
/// An SMTP server, in-process, on a loopback port — enough of one that MailKit will complete a
/// real conversation with it.
///
/// Test-plan N-10 asks for "SMTP transport itself (connect, STARTTLS, auth)" and has sat at "not
/// covered — needs a real or fake SMTP server" since the suite was written. The assumption behind
/// that was that a server means a container. It does not: a <see cref="TcpListener"/> and a
/// self-signed certificate are a server, and the half that matters is the client's — the code
/// under test runs MailKit's genuine <c>SmtpClient</c> over a genuine socket, negotiates a genuine
/// TLS handshake, and speaks a genuine SASL exchange.
///
/// That distinction is the whole value of the row. A mocked <c>ISmtpClient</c> can prove
/// <c>ConnectAsync</c> was called with the configured port; it cannot prove the server was ever
/// asked to upgrade the connection, and it cannot notice a configuration in which the password
/// crosses the wire before it does. This can, because it records what actually arrived and in what
/// order — and, for every byte after STARTTLS, whether it arrived encrypted.
/// </summary>
internal sealed class FakeSmtpServer : IAsyncDisposable
{
    /// <summary>How the server should misbehave, so the failure paths are reachable.</summary>
    internal enum Behaviour
    {
        /// <summary>Speak the full conversation and accept the message.</summary>
        Accept,

        /// <summary>
        /// A plaintext-only server: never advertises STARTTLS, and offers AUTH PLAIN in the clear
        /// anyway.
        ///
        /// Both halves matter, and the second one was missing at first. Withholding AUTH until
        /// encrypted is what a careful server does — and it meant a client that had silently
        /// downgraded still failed, because there was nothing to authenticate WITH. The test
        /// passed for the server's reason instead of the client's, and a mutation swapping
        /// <c>StartTls</c> for <c>StartTlsWhenAvailable</c> walked straight through it. Offering
        /// AUTH in the clear is what a misconfigured or hostile server does, and it is the only
        /// arrangement in which refusing to downgrade is observably the client's decision.
        /// </summary>
        NoStartTls,

        /// <summary>Reject the credentials with 535, the way a wrong app password does.</summary>
        RejectAuth,

        /// <summary>Accept the connection and then say nothing at all — a black hole.</summary>
        Silent,

        /// <summary>Drop the connection in the middle of the message body.</summary>
        DropDuringData,
    }

    private readonly TcpListener _listener;
    private readonly Behaviour _behaviour;
    private readonly bool _tlsOnConnect;
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _accepting;
    private readonly Lock _gate = new();

    private readonly List<string> _commands = [];
    private readonly List<string> _plaintextCommands = [];
    private string? _authUser;
    private string? _authPassword;
    private string? _data;
    private bool _offeredAuth;
    private bool _offeredAuthInTheClear;

    private FakeSmtpServer(Behaviour behaviour, bool tlsOnConnect)
    {
        _behaviour = behaviour;
        _tlsOnConnect = tlsOnConnect;
        _certificate = SelfSignedCertificate();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _accepting = Task.Run(AcceptLoopAsync);
    }

    internal static FakeSmtpServer Start(Behaviour behaviour = Behaviour.Accept, bool tlsOnConnect = false)
        => new(behaviour, tlsOnConnect);

    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Every command verb line the client sent, in order, across both phases.</summary>
    internal IReadOnlyList<string> Commands
    {
        get { lock (_gate) { return [.. _commands]; } }
    }

    /// <summary>
    /// Only the commands that arrived BEFORE the TLS handshake — that is, the ones anyone watching
    /// the network could read. Nothing carrying a credential may appear here.
    /// </summary>
    internal IReadOnlyList<string> PlaintextCommands
    {
        get { lock (_gate) { return [.. _plaintextCommands]; } }
    }

    /// <summary>
    /// Whether the server advertised a usable AUTH mechanism over an unencrypted connection.
    ///
    /// Asserted rather than assumed, so "the client refused to authenticate" cannot quietly become
    /// "there was nothing to authenticate with".
    /// </summary>
    internal bool OfferedAuthInTheClear { get { lock (_gate) { return _offeredAuthInTheClear; } } }

    internal bool OfferedAuth { get { lock (_gate) { return _offeredAuth; } } }

    internal string? AuthUser { get { lock (_gate) { return _authUser; } } }

    internal string? AuthPassword { get { lock (_gate) { return _authPassword; } } }

    /// <summary>The raw RFC 5322 message the client transmitted after DATA, dot-unstuffed.</summary>
    internal string? DeliveredMessage { get { lock (_gate) { return _data; } } }

    // --- the conversation ----------------------------------------------------------------------

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown, not a failure.
        }
        catch (SocketException)
        {
            // Listener closed underneath us.
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            Stream stream = client.GetStream();
            var encrypted = false;

            if (_tlsOnConnect)
            {
                stream = await UpgradeAsync(stream);
                encrypted = true;
            }

            if (_behaviour == Behaviour.Silent)
            {
                // No greeting. MailKit blocks reading it, which is what the timeout is for.
                await Task.Delay(Timeout.Infinite, _shutdown.Token);
                return;
            }

            var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            await WriteAsync(stream, "220 fake.intellilect.test ESMTP ready");

            while (await reader.ReadLineAsync(_shutdown.Token) is { } line)
            {
                Record(line, encrypted);
                var verb = line.Split(' ', 2)[0].ToUpperInvariant();

                switch (verb)
                {
                    case "EHLO":
                        await WriteAsync(stream, Greeting(encrypted));
                        break;

                    case "STARTTLS":
                        await WriteAsync(stream, "220 Go ahead");
                        stream = await UpgradeAsync(stream);
                        encrypted = true;
                        reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                        break;

                    case "AUTH":
                        await HandleAuthAsync(stream, reader, line);
                        break;

                    case "MAIL":
                    case "RCPT":
                        await WriteAsync(stream, "250 2.1.0 OK");
                        break;

                    case "DATA":
                        await WriteAsync(stream, "354 End data with <CR><LF>.<CR><LF>");
                        if (_behaviour == Behaviour.DropDuringData)
                        {
                            client.Close();
                            return;
                        }
                        await ReadDataAsync(reader);
                        await WriteAsync(stream, "250 2.0.0 OK: queued as FAKE1");
                        break;

                    case "QUIT":
                        await WriteAsync(stream, "221 2.0.0 Bye");
                        return;

                    default:
                        await WriteAsync(stream, "502 5.5.2 Command not implemented");
                        break;
                }
            }
        }
        catch (Exception)
        {
            // A client that hangs up mid-conversation is one of the cases under test, and every
            // assertion is made on the client side. Nothing here should fail a test on its own.
        }
    }

    /// <summary>
    /// The EHLO response, which is where STARTTLS is offered or withheld.
    ///
    /// Only AUTH PLAIN is advertised, so the exchange is deterministic — MailKit picks by its own
    /// preference order when several are on offer. Normally it appears only once encrypted, which
    /// is what a real server does; under <see cref="Behaviour.NoStartTls"/> it is offered in the
    /// clear, so that a client which declined to encrypt has something it *could* have
    /// authenticated with and its refusal is its own.
    /// </summary>
    private string Greeting(bool encrypted)
    {
        var lines = new List<string> { "250-fake.intellilect.test" };

        if (!encrypted && _behaviour != Behaviour.NoStartTls)
        {
            lines.Add("250-STARTTLS");
        }

        if (encrypted || _behaviour == Behaviour.NoStartTls)
        {
            lines.Add("250-AUTH PLAIN");
            lock (_gate)
            {
                _offeredAuth = true;
                _offeredAuthInTheClear |= !encrypted;
            }
        }

        lines.Add("250 HELP");
        return string.Join("\r\n", lines);
    }

    private async Task HandleAuthAsync(Stream stream, StreamReader reader, string line)
    {
        // AUTH PLAIN <base64> carries \0user\0password in one token; the initial response may also
        // arrive on the following line if the client sends a bare "AUTH PLAIN".
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? token = parts.Length >= 3 ? parts[2] : null;

        if (token is null)
        {
            await WriteAsync(stream, "334 ");
            token = await reader.ReadLineAsync(_shutdown.Token);
        }

        if (token is not null)
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token)).Split('\0');
            lock (_gate)
            {
                _authUser = decoded.Length > 1 ? decoded[1] : null;
                _authPassword = decoded.Length > 2 ? decoded[2] : null;
            }
        }

        await WriteAsync(
            stream,
            _behaviour == Behaviour.RejectAuth
                ? "535 5.7.8 Username and Password not accepted"
                : "235 2.7.0 Accepted");
    }

    private async Task ReadDataAsync(StreamReader reader)
    {
        var body = new StringBuilder();
        while (await reader.ReadLineAsync(_shutdown.Token) is { } line && line != ".")
        {
            // Dot-unstuffing, per RFC 5321 §4.5.2.
            body.AppendLine(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line);
        }

        lock (_gate)
        {
            _data = body.ToString();
        }
    }

    private void Record(string line, bool encrypted)
    {
        lock (_gate)
        {
            _commands.Add(line);
            if (!encrypted)
            {
                _plaintextCommands.Add(line);
            }
        }
    }

    private static async Task WriteAsync(Stream stream, string response)
    {
        var bytes = Encoding.ASCII.GetBytes(response + "\r\n");
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private async Task<Stream> UpgradeAsync(Stream stream)
    {
        var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsServerAsync(_certificate, false, checkCertificateRevocation: false);
        return ssl;
    }

    /// <summary>
    /// A throwaway certificate for 127.0.0.1, generated per server.
    ///
    /// It is self-signed, so no client trusts it by default — which is correct, and is why the
    /// tests hand their <c>SmtpClient</c> a validation callback that accepts exactly this one.
    /// Trust is the single thing being faked; the handshake itself is real.
    /// </summary>
    private static X509Certificate2 SelfSignedCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        // Round-tripped through PKCS#12 so the private key is associated with the certificate in
        // the form SslStream needs on every platform.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    private bool _disposed;

    /// <summary>
    /// Idempotent: one test stops the server early to get a port with nothing on it, and then the
    /// enclosing <c>await using</c> disposes it again.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        await _shutdown.CancelAsync();
        _listener.Stop();

        try
        {
            await _accepting;
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        _shutdown.Dispose();
        _certificate.Dispose();
    }
}
