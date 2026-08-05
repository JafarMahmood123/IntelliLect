using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UserManagementService.Application.Abstractions;
using UserManagementService.Infrastructure;
using UserManagementService.Infrastructure.Configuration;

namespace UserManagementService.UnitTests.Configuration;

/// <summary>
/// How this service reaches the four downstream <c>/api/internal</c> APIs — work-plan §14.3/14.4.
///
/// These settings used to be read by string index, with an inline default per read, spread
/// between the composition root and each client. That is not only untidy: it made a missing value
/// silent. A blank internal secret meant the client omitted the header, the far side refused the
/// call (those routes fail closed by design), and the symptom was a super-admin page of empty
/// panels — with the 401s reading as the other service being down rather than as a setting nobody
/// had set.
///
/// Writing these tests found exactly that: <c>LiveAssistant__*</c> and <c>RagService__*</c> were
/// absent from UMS's compose file altogether, so every call it made to those two services was
/// being refused.
/// </summary>
public sealed class InternalServiceOptionsTests
{
    private const string Secret = "shared-internal-secret";

    private static Dictionary<string, string?> CompleteSettings() => new()
    {
        ["ConnectionStrings:Database"] = "Host=localhost;Database=test",
        ["Jwt:SecretKey"] = "a-signing-key-of-at-least-thirty-two-chars",
        ["Jwt:Issuer"] = "IntelliLect",
        ["Jwt:Audience"] = "IntelliLect",
        ["RabbitMq:Host"] = "localhost",
        ["RabbitMq:Username"] = "guest",
        ["RabbitMq:Password"] = "guest",
        ["ClassroomService:BaseUrl"] = "http://classroom-service:8080",
        ["ClassroomService:InternalApiSecret"] = Secret,
        ["ClassroomService:TimeoutSeconds"] = "11",
        ["StreamingService:BaseUrl"] = "http://streaming-service:8080",
        ["StreamingService:InternalApiSecret"] = Secret,
        ["StreamingService:TimeoutSeconds"] = "5",
        ["LiveAssistant:BaseUrl"] = "http://live-assistant-service:8080",
        ["LiveAssistant:InternalApiSecret"] = Secret,
        ["LiveAssistant:TimeoutSeconds"] = "5",
        ["RagService:BaseUrl"] = "http://rag-service:8080",
        ["RagService:InternalApiSecret"] = Secret,
        ["RagService:TimeoutSeconds"] = "10",
    };

    private static ServiceProvider Build(Action<Dictionary<string, string?>>? mutate = null)
    {
        var settings = CompleteSettings();
        mutate?.Invoke(settings);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection()
            .AddLogging()
            .AddInfrastructure(configuration)
            .BuildServiceProvider();
    }

    /// <summary>
    /// Reads the options the way `ValidateOnStart` does — resolving `.Value` runs the validators.
    /// A test that only builds the container would pass with any configuration at all, because
    /// binding is lazy.
    /// </summary>
    private static T Resolve<T>(ServiceProvider provider) where T : class
        => provider.GetRequiredService<IOptions<T>>().Value;

    // --- binding ------------------------------------------------------------------

    [Fact]
    public void Each_downstream_service_binds_its_own_section()
    {
        using var provider = Build();

        Assert.Equal("http://classroom-service:8080", Resolve<ClassroomServiceOptions>(provider).BaseUrl);
        Assert.Equal("http://streaming-service:8080", Resolve<StreamingServiceOptions>(provider).BaseUrl);
        Assert.Equal("http://live-assistant-service:8080", Resolve<LiveAssistantOptions>(provider).BaseUrl);
        Assert.Equal("http://rag-service:8080", Resolve<RagServiceOptions>(provider).BaseUrl);
    }

    [Fact]
    public void The_configured_timeout_is_the_one_used()
    {
        // Streaming and LiveAssistant were hard-coded to 5s in C# while compose supplied a
        // TimeoutSeconds nothing read. A setting that is provided and ignored is worse than
        // either value, because the next person to change it will believe they have.
        using var provider = Build();

        Assert.Equal(11, Resolve<ClassroomServiceOptions>(provider).TimeoutSeconds);
        Assert.Equal(5, Resolve<StreamingServiceOptions>(provider).TimeoutSeconds);
        Assert.Equal(5, Resolve<LiveAssistantOptions>(provider).TimeoutSeconds);
    }

    [Fact]
    public void An_unset_timeout_falls_back_to_a_usable_default()
    {
        // The one setting here that genuinely has a sane default. The URL and the secret do not.
        using var provider = Build(s => s.Remove("RagService:TimeoutSeconds"));

        Assert.Equal(10, Resolve<RagServiceOptions>(provider).TimeoutSeconds);
    }

    [Fact]
    public void All_four_services_share_one_internal_secret()
    {
        // They are one value in the .env by design — every service's INTERNAL_API_SECRET. A test
        // that let them drift apart would hide the most likely misconfiguration: setting three.
        using var provider = Build();

        Assert.Equal(Secret, Resolve<ClassroomServiceOptions>(provider).InternalApiSecret);
        Assert.Equal(Secret, Resolve<StreamingServiceOptions>(provider).InternalApiSecret);
        Assert.Equal(Secret, Resolve<LiveAssistantOptions>(provider).InternalApiSecret);
        Assert.Equal(Secret, Resolve<RagServiceOptions>(provider).InternalApiSecret);
    }

    // --- fail fast ----------------------------------------------------------------

    [Theory]
    [InlineData("ClassroomService:InternalApiSecret")]
    [InlineData("StreamingService:InternalApiSecret")]
    [InlineData("LiveAssistant:InternalApiSecret")]
    [InlineData("RagService:InternalApiSecret")]
    public void A_missing_internal_secret_is_refused_and_says_what_to_set(string key)
    {
        // The failure this whole item is about. Without it the service starts perfectly and only
        // misbehaves when a super admin opens a page, as a 401 from a service that is running.
        //
        // The message is asserted on its ACTIONABLE half, not on the section name: the default
        // DataAnnotations text is "DataAnnotation validation failed for 'RagServiceOptions'…",
        // which already contains the section name by accident of the type name — so a test
        // looking for that would pass with no custom message at all, and the operator would be
        // told a field is required without being told what it is or where it comes from.
        var section = key.Split(':')[0];
        using var provider = Build(s => s.Remove(key));

        var error = Assert.ThrowsAny<OptionsValidationException>(() => ResolveFor(provider, key));

        var message = string.Join(" ", error.Failures);
        Assert.Contains($"{section}:InternalApiSecret", message);
        Assert.Contains("INTERNAL_API_SECRET", message);
    }

    [Theory]
    [InlineData("ClassroomService:BaseUrl")]
    [InlineData("StreamingService:BaseUrl")]
    [InlineData("LiveAssistant:BaseUrl")]
    [InlineData("RagService:BaseUrl")]
    public void A_missing_base_url_is_refused_rather_than_defaulted(string key)
    {
        // Previously each read carried its own `?? "http://some-service:8080"`, so a typo'd key
        // silently kept working against the compose default — and stopped working the moment the
        // deployment was not compose.
        var section = key.Split(':')[0];
        using var provider = Build(s => s.Remove(key));

        var error = Assert.ThrowsAny<OptionsValidationException>(() => ResolveFor(provider, key));

        Assert.Contains($"{section}:BaseUrl", string.Join(" ", error.Failures));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void A_timeout_that_HttpClient_would_reject_is_refused_first(string configured)
    {
        // HttpClient.Timeout throws on zero or negative. A typo'd 0 used to take the service down
        // with an ArgumentOutOfRangeException naming neither the setting nor the service.
        using var provider = Build(s => s["RagService:TimeoutSeconds"] = configured);

        Assert.ThrowsAny<OptionsValidationException>(() => Resolve<RagServiceOptions>(provider));
    }

    [Fact]
    public void A_blank_secret_is_refused_the_same_as_a_missing_one()
    {
        // Whitespace is the shape an empty compose interpolation leaves behind, and it would
        // otherwise satisfy a plain "is it set" check while behaving exactly like absent.
        using var provider = Build(s => s["RagService:InternalApiSecret"] = "   ");

        Assert.ThrowsAny<OptionsValidationException>(() => Resolve<RagServiceOptions>(provider));
    }

    // --- the clients actually get it ----------------------------------------------

    [Theory]
    [InlineData(nameof(IClassroomInternalClient), "http://classroom-service:8080/", 11)]
    [InlineData(nameof(IStreamingInternalClient), "http://streaming-service:8080/", 5)]
    [InlineData(nameof(ILiveAssistantInternalClient), "http://live-assistant-service:8080/", 5)]
    [InlineData(nameof(IRagAdminClient), "http://rag-service:8080/", 10)]
    public void Every_internal_client_is_built_with_its_address_timeout_and_secret(
        string clientName, string expectedBaseAddress, int expectedTimeoutSeconds)
    {
        // The options being right is only half of it. The header is attached once at
        // registration, and a client that never receives it produces a 401 from a service that
        // is running perfectly — among the least obvious failures in this system to diagnose.
        using var provider = Build();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        var http = factory.CreateClient(clientName);

        Assert.Equal(expectedBaseAddress, http.BaseAddress?.ToString());
        Assert.Equal(TimeSpan.FromSeconds(expectedTimeoutSeconds), http.Timeout);
        Assert.Equal(
            Secret,
            Assert.Single(http.DefaultRequestHeaders.GetValues(
                InternalServiceRegistration.InternalSecretHeaderName)));
    }

    /// <summary>Resolves whichever options type owns <paramref name="key"/>'s section.</summary>
    private static void ResolveFor(ServiceProvider provider, string key)
    {
        switch (key.Split(':')[0])
        {
            case ClassroomServiceOptions.SectionName:
                Resolve<ClassroomServiceOptions>(provider);
                break;
            case StreamingServiceOptions.SectionName:
                Resolve<StreamingServiceOptions>(provider);
                break;
            case LiveAssistantOptions.SectionName:
                Resolve<LiveAssistantOptions>(provider);
                break;
            case RagServiceOptions.SectionName:
                Resolve<RagServiceOptions>(provider);
                break;
            default:
                throw new ArgumentException($"No options type owns '{key}'.", nameof(key));
        }
    }
}
