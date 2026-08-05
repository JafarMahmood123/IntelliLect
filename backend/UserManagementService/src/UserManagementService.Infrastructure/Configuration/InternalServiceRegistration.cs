using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace UserManagementService.Infrastructure.Configuration;

/// <summary>
/// Registration for the four downstream <c>/api/internal</c> clients, so each one is wired the
/// same way rather than four hand-rolled blocks that drifted apart.
/// </summary>
public static class InternalServiceRegistration
{
    /// <summary>
    /// Binds a section to <typeparamref name="TOptions"/> and validates it when the app STARTS,
    /// not when the setting is first used.
    ///
    /// The distinction is the whole point of §14.4. A blank internal secret does not break
    /// startup on its own — it breaks the first super-admin page load, an hour later, as a 401
    /// from another service. `ValidateOnStart` turns that into a refusal to boot that names the
    /// key.
    /// </summary>
    public static IServiceCollection AddInternalServiceOptions<TOptions>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName)
        where TOptions : InternalServiceOptions
    {
        services
            .AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateDataAnnotations()
            // Names the section, because "The BaseUrl field is required" is not actionable when
            // four sections have a BaseUrl.
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.BaseUrl),
                $"{sectionName}:BaseUrl must be set (e.g. http://{sectionName.ToLowerInvariant()}:8080).")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.InternalApiSecret),
                $"{sectionName}:InternalApiSecret must be set and must match that service's "
                + "INTERNAL_API_SECRET, or every call to it will be refused.")
            .ValidateOnStart();

        return services;
    }

    /// <summary>
    /// Registers a typed HttpClient whose base address, timeout and internal-secret header all
    /// come from <typeparamref name="TOptions"/>.
    ///
    /// The header is attached HERE, once, rather than in each client's constructor. That is not
    /// only tidier: a client that forgets it produces a 401 from a service that is running
    /// perfectly, which is among the least obvious failures in this system to diagnose.
    /// </summary>
    public static IServiceCollection AddInternalHttpClient<TInterface, TImplementation, TOptions>(
        this IServiceCollection services)
        where TInterface : class
        where TImplementation : class, TInterface
        where TOptions : InternalServiceOptions
    {
        services.AddHttpClient<TInterface, TImplementation>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<TOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                InternalSecretHeaderName, options.InternalApiSecret);
        });

        return services;
    }

    public const string InternalSecretHeaderName = "X-Internal-Secret";
}
