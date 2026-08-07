using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StreamingService.Infrastructure;

namespace StreamingService.UnitTests;

/// <summary>
/// Settings with no safe default must stop this service at startup — test-plan M-02, M-18, M-19.
///
/// UserManagementService has had this file since §7.9. StreamingService never did, so §7.4's fix
/// here — deleting the literal `Jwt:SecretKey` fallback that made a keyless service accept tokens
/// anyone could forge — was **never pinned**. Two mutations proved it: removing the media
/// credential checks added in §14b changed nothing at all, because the only test that builds this
/// container supplies every key and no test ever asks what happens without one.
///
/// The media pair is the reason this file exists now. `appsettings.json` shipped
/// `ApiKey = "devkey"` and `ApiSecret = "super_secret_livekit_key_for_development"` — LiveKit's
/// **published** development defaults — so a service started without them did not fail, it signed
/// join tokens with a secret printed in this repository and in LiveKit's documentation. A join
/// token is entry to the media room rather than a step towards it (§7.4d), so that is not a
/// degraded configuration; it is an open door with a documented key.
///
/// These run against the real composition root, so they fail if somebody reintroduces a fallback
/// rather than merely if the helper changes.
/// </summary>
public sealed class RequiredSettingsTests
{
    private static Dictionary<string, string?> FullConfiguration() => new()
    {
        ["ConnectionStrings:Database"] = "Host=localhost;Database=test",
        ["Jwt:SecretKey"] = "a-signing-key-of-at-least-thirty-two-chars",
        ["Jwt:Issuer"] = "IntelliLect",
        ["Jwt:Audience"] = "IntelliLect",
        ["RabbitMq:Host"] = "localhost",
        ["RabbitMq:Username"] = "guest",
        ["RabbitMq:Password"] = "guest",
        ["LiveKit:ApiKey"] = "test-api-key",
        ["LiveKit:ApiSecret"] = "test-api-secret",
        ["LiveKit:Host"] = "ws://localhost:7880",
    };

    private static IConfiguration Without(string omittedKey)
    {
        var settings = FullConfiguration();
        settings.Remove(omittedKey);
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    [Theory]
    [InlineData("Jwt:SecretKey")]
    [InlineData("RabbitMq:Username")]
    [InlineData("RabbitMq:Password")]
    [InlineData("LiveKit:ApiKey")]
    [InlineData("LiveKit:ApiSecret")]
    public void A_missing_required_setting_stops_startup(string key)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddInfrastructure(Without(key)));

        // The message has to name the key, and the environment variable it maps to. A startup
        // failure that says only "configuration error" sends somebody reading source at the moment
        // the service is down.
        Assert.Contains(key, exception.Message);
        Assert.Contains(key.Replace(":", "__"), exception.Message);
    }

    [Theory]
    [InlineData("Jwt:SecretKey")]
    [InlineData("RabbitMq:Username")]
    [InlineData("RabbitMq:Password")]
    [InlineData("LiveKit:ApiKey")]
    [InlineData("LiveKit:ApiSecret")]
    public void A_blank_required_setting_counts_as_missing(string key)
    {
        // The common shape of this failure: a variable that is set but unresolved. It reads as
        // configured to anything checking for null, which is how a blank signing key becomes a
        // service that validates tokens signed with nothing.
        var settings = FullConfiguration();
        settings[key] = "   ";

        Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddInfrastructure(
                new ConfigurationBuilder().AddInMemoryCollection(settings).Build()));
    }

    [Fact]
    public void A_fully_configured_service_starts()
    {
        // The other half: the guard must not be so eager that a correct configuration is refused.
        var services = new ServiceCollection().AddInfrastructure(Without("nothing"));

        Assert.NotEmpty(services);
    }

    [Fact]
    public void The_committed_settings_supply_no_media_credential_to_fall_back_on()
    {
        // The rule above proves the code demands these. This proves the file no longer answers.
        // Both halves are needed: the check could be reinstated and quietly satisfied by the
        // literal coming back, which is exactly how it was before — the check was absent AND the
        // value was present, and each made the other invisible.
        var settings = File.ReadAllText(Path.Combine(
            ServiceRoot(), "src", "StreamingService.Api", "appsettings.json"));

        using var document = System.Text.Json.JsonDocument.Parse(settings);
        var livekit = document.RootElement.GetProperty("LiveKit");

        Assert.Equal(string.Empty, livekit.GetProperty("ApiKey").GetString());
        Assert.Equal(string.Empty, livekit.GetProperty("ApiSecret").GetString());
    }

    private static string ServiceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, "src", "StreamingService.Application")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
