using EmailService.Infrastructure;
using EmailService.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EmailService.UnitTests;

/// <summary>
/// The SMTP settings are validated when the service STARTS, not when the first email is sent
/// (test-plan N-10, work-plan §14.4).
///
/// This is the defect the N-10 work turned up, and it is worth stating precisely because the code
/// looked correct. The sender read:
///
///     var appPassword = settings["AppPassword"]
///         ?? throw new InvalidOperationException("EmailSettings:AppPassword is required.");
///
/// and <c>appsettings.json</c>, two directories away, shipped <c>"AppPassword": ""</c>. An empty
/// string is not null. The guard could never fire, so a deployment that forgot
/// <c>EmailSettings__AppPassword</c> started cleanly, answered <c>/health</c> with "ok", bound all
/// five queues, and then failed every single send — three retries each, then the error queue.
/// Nobody watches an error queue for the message that told a student their account was approved.
///
/// The repository's own <c>Required()</c> helper already says the rule this missed: "Treats empty
/// as missing: a blank password is not a configured one." It was applied to the broker credentials
/// in the same file and not to these.
/// </summary>
public sealed class EmailSettingsValidationTests
{
    /// <summary>
    /// The real composition root, with the broker credentials it also demands. Only the
    /// EmailSettings values vary per case.
    /// </summary>
    private static IConfiguration Configuration(params (string Key, string? Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["RabbitMq:Username"] = "guest",
            ["RabbitMq:Password"] = "guest",
            ["EmailSettings:SenderEmail"] = "sender@intellilect.test",
            ["EmailSettings:AppPassword"] = "app-password",
            ["EmailSettings:SmtpHost"] = "smtp.gmail.com",
            ["EmailSettings:SmtpPort"] = "587",
        };

        foreach (var (key, value) in overrides)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <summary>
    /// Resolves the options the way <c>ValidateOnStart</c> does at boot, so a validation failure
    /// surfaces here as the exception the host would print and exit on.
    /// </summary>
    private static EmailSettings Resolve(IConfiguration configuration)
        => new ServiceCollection()
            .AddInfrastructure(configuration)
            .BuildServiceProvider()
            .GetRequiredService<IOptions<EmailSettings>>()
            .Value;

    [Fact]
    public void A_complete_configuration_binds()
    {
        // The vacuum guard. Every case below expects a throw, and a registration that threw on
        // everything would satisfy all of them.
        var settings = Resolve(Configuration());

        Assert.Equal("sender@intellilect.test", settings.SenderEmail);
        Assert.Equal("app-password", settings.AppPassword);
        Assert.Equal(587, settings.SmtpPort);
        Assert.Equal(SmtpSecurity.StartTls, settings.Security);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_or_absent_password_refuses_to_start(string? value)
    {
        // The empty string is the case that mattered — it is what shipped, and it is the one the
        // old `?? throw` let through. The other two are here so the rule is about the value being
        // unusable rather than about one particular spelling of missing.
        var failure = Assert.ThrowsAny<Exception>(
            () => Resolve(Configuration(("EmailSettings:AppPassword", value))));

        Assert.Contains("AppPassword", failure.Message, StringComparison.Ordinal);
        // Names the environment variable, because the person reading it is looking at a compose
        // file and not at a C# class.
        Assert.Contains("EmailSettings__AppPassword", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_or_absent_sender_refuses_to_start(string? value)
    {
        var failure = Assert.ThrowsAny<Exception>(
            () => Resolve(Configuration(("EmailSettings:SenderEmail", value))));

        Assert.Contains("SenderEmail", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_address_that_is_not_an_address_refuses_to_start()
    {
        // MimeKit throws a ParseException when it builds the From header, which surfaces from
        // inside a consumer as a failed send with no hint that the CONFIGURATION is what is wrong.
        var failure = Assert.ThrowsAny<Exception>(
            () => Resolve(Configuration(("EmailSettings:SenderEmail", "not-an-address"))));

        Assert.Contains("SenderEmail", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("70000")]
    public void A_port_outside_the_range_refuses_to_start(string value)
    {
        var failure = Assert.ThrowsAny<Exception>(
            () => Resolve(Configuration(("EmailSettings:SmtpPort", value))));

        Assert.Contains("SmtpPort", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_port_that_is_not_a_number_refuses_to_start_instead_of_becoming_587()
    {
        // `int.TryParse(settings["SmtpPort"], out var p) ? p : 587` returned 587 for this, so a
        // deployment aiming at 465 with a stray character got 587 and a STARTTLS negotiation
        // against a server that expects TLS from the first byte — a handshake error naming
        // neither the port nor the setting.
        Assert.ThrowsAny<Exception>(() => Resolve(Configuration(("EmailSettings:SmtpPort", "58x"))));
    }

    [Fact]
    public void A_timeout_outside_the_range_refuses_to_start()
    {
        Assert.ThrowsAny<Exception>(() => Resolve(Configuration(("EmailSettings:TimeoutSeconds", "0"))));
        Assert.ThrowsAny<Exception>(() => Resolve(Configuration(("EmailSettings:TimeoutSeconds", "10000"))));
    }

    [Fact]
    public void The_defaults_are_the_ones_the_deployment_actually_uses()
    {
        // Only the two credentials are genuinely required; everything else has a default, and a
        // default that disagrees with the compose file is a setting that behaves differently in
        // development from production for no stated reason.
        var settings = Resolve(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["RabbitMq:Username"] = "guest",
                ["RabbitMq:Password"] = "guest",
                ["EmailSettings:SenderEmail"] = "sender@intellilect.test",
                ["EmailSettings:AppPassword"] = "app-password",
            }).Build());

        Assert.Equal("IntelliLect", settings.SenderName);
        Assert.Equal("smtp.gmail.com", settings.SmtpHost);
        Assert.Equal(587, settings.SmtpPort);
        Assert.Equal(30, settings.TimeoutSeconds);
    }

    [Fact]
    public void Validation_runs_at_STARTUP_and_not_at_the_first_send()
    {
        // Every case above resolves IOptions<>.Value, which runs the validators whenever it is
        // first touched — so all of them stay green with `.ValidateOnStart()` deleted, and the
        // service would go back to discovering a blank password on its first email instead of at
        // boot. That is precisely the defect this row closed, so it needs its own assertion.
        //
        // `.ValidateOnStart()` registers a startup validator that the host runs before it serves
        // anything; its presence is what makes "refuses to start" true rather than aspirational.
        var registered = new ServiceCollection()
            .AddInfrastructure(Configuration())
            .Select(descriptor => descriptor.ServiceType.Name)
            .ToList();

        Assert.Contains("IStartupValidator", registered);
    }

    [Fact]
    public void The_shipped_appsettings_supplies_no_blank_credential()
    {
        // The other half of the defect, and the half a unit test would normally never see: the
        // guard was correct in isolation and dead in place, because a FILE two directories away
        // supplied an empty string for the value it was guarding. Nothing above would notice that
        // coming back.
        var appsettings = File.ReadAllText(Path.Combine(ApiProjectRoot(), "appsettings.json"));
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(ApiProjectRoot(), "appsettings.json"))
            .Build();

        Assert.True(
            string.IsNullOrEmpty(configuration["EmailSettings:AppPassword"]),
            "appsettings.json now supplies an AppPassword — if it is a real one it is a credential "
            + "in the git history, and if it is blank it is the defect this row closed.");
        Assert.DoesNotContain("\"AppPassword\"", appsettings);
        Assert.DoesNotContain("\"SenderEmail\"", appsettings);
        // And the settings that DO belong there are still there, so this cannot be satisfied by
        // deleting the section.
        Assert.Contains("\"SmtpHost\"", appsettings);
    }

    /// <summary>Walks up from the test binary to the Api project that owns appsettings.json.</summary>
    private static string ApiProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "EmailService.Api")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "EmailService.Api");
    }
}
