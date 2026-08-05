using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UserManagementService.Infrastructure;

namespace UserManagementService.UnitTests.Configuration;

/// <summary>
/// Settings with no safe default must stop the service at startup — test-plan M-02.
///
/// Every one of these was, at some point, a value with a fallback. The broker credentials were
/// hardcoded; the JWT signing key defaulted to a literal that is public in this repository's
/// history, so a missing key did not break the service, it made it accept tokens anyone could
/// forge. The pattern that replaced them only works if it keeps working, and nothing about
/// <c>?? "default"</c> looks wrong at a glance — which is exactly why it survived so long.
///
/// These run against the real composition root, so they fail if someone reintroduces a fallback
/// rather than merely if the helper changes.
/// </summary>
public sealed class RequiredSettingsTests
{
    private static IConfiguration ConfigurationWithout(string omittedKey)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = "Host=localhost;Database=test",
            ["Jwt:SecretKey"] = "a-signing-key-of-at-least-thirty-two-chars",
            ["Jwt:Issuer"] = "IntelliLect",
            ["Jwt:Audience"] = "IntelliLect",
            ["RabbitMq:Host"] = "localhost",
            ["RabbitMq:Username"] = "guest",
            ["RabbitMq:Password"] = "guest",
        };

        settings.Remove(omittedKey);
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    [Theory]
    [InlineData("Jwt:SecretKey")]
    [InlineData("RabbitMq:Username")]
    [InlineData("RabbitMq:Password")]
    public void A_missing_required_setting_stops_startup(string key)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddInfrastructure(ConfigurationWithout(key)));

        // The message has to name the key. A startup failure that says only "configuration error"
        // sends someone reading source at the moment the service is down.
        Assert.Contains(key, exception.Message);
        Assert.Contains(key.Replace(":", "__"), exception.Message);
    }

    [Theory]
    [InlineData("Jwt:SecretKey")]
    [InlineData("RabbitMq:Username")]
    [InlineData("RabbitMq:Password")]
    public void A_blank_required_setting_counts_as_missing(string key)
    {
        // An empty environment variable is the common shape of this failure — a variable that is
        // set but unresolved reads as configured to anything checking for null.
        var settings = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = "Host=localhost;Database=test",
                ["Jwt:SecretKey"] = "a-signing-key-of-at-least-thirty-two-chars",
                ["RabbitMq:Username"] = "guest",
                ["RabbitMq:Password"] = "guest",
                [key] = "   ",
            })
            .Build();

        Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddInfrastructure(settings));
    }

    [Fact]
    public void A_fully_configured_service_starts()
    {
        // The other half: the guard must not be so eager that a correct configuration is refused.
        var services = new ServiceCollection().AddInfrastructure(ConfigurationWithout("nothing"));

        Assert.NotEmpty(services);
    }
}
