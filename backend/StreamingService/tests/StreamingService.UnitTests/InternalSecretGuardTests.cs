using System.Reflection;
using StreamingService.Presentation.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace StreamingService.UnitTests;

/// <summary>
/// The guard on service-to-service routes (test-plan B-08, B-09).
///
/// These endpoints are not proxied by nginx, so the usual argument is that nothing outside can
/// reach them. That is a fact about today's network topology, not a property of the code — and it
/// stops being true the moment another container is compromised or a port is published by
/// accident. The header is what makes it a guarantee.
/// </summary>
public sealed class InternalSecretGuardTests
{
    private static AuthorizationFilterContext Context(string? configuredSecret, string? sentHeader)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configuredSecret is null
                ? []
                : new Dictionary<string, string?> { ["Internal:ApiSecret"] = configuredSecret })
            .Build();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IConfiguration>(configuration)
                .BuildServiceProvider(),
        };

        if (sentHeader is not null)
        {
            httpContext.Request.Headers[InternalSecretAttribute.HeaderName] = sentHeader;
        }

        return new AuthorizationFilterContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()), []);
    }

    private static IActionResult? Run(string? configuredSecret, string? sentHeader)
    {
        var context = Context(configuredSecret, sentHeader);
        new InternalSecretAttribute().OnAuthorization(context);
        return context.Result;
    }

    [Fact]
    public void A_matching_secret_is_let_through()
    {
        // Null result means the filter did not short-circuit — the action runs.
        Assert.Null(Run("s3cret", "s3cret"));
    }

    [Fact]
    public void A_missing_header_is_refused()
    {
        Assert.IsType<UnauthorizedResult>(Run("s3cret", sentHeader: null));
    }

    [Fact]
    public void A_wrong_secret_is_refused()
    {
        Assert.IsType<UnauthorizedResult>(Run("s3cret", "not-the-secret"));
    }

    [Fact]
    public void An_unconfigured_secret_refuses_rather_than_admits()
    {
        // A guard that switches itself off when misconfigured is worse than no guard, because it
        // is believed in. This is also what the Python services do with the same secret, so one
        // missing environment variable cannot open half the stack while closing the other half.
        Assert.IsType<UnauthorizedResult>(Run(configuredSecret: null, sentHeader: null));
        Assert.IsType<UnauthorizedResult>(Run(configuredSecret: null, sentHeader: "anything"));
        Assert.IsType<UnauthorizedResult>(Run(configuredSecret: "   ", sentHeader: "   "));
    }

    [Fact]
    public void Comparison_is_exact()
    {
        // Case, whitespace and prefixes are all a mismatch. A prefix in particular: accepting one
        // would turn a length-guessing attack into a value-guessing one.
        Assert.IsType<UnauthorizedResult>(Run("s3cret", "S3CRET"));
        Assert.IsType<UnauthorizedResult>(Run("s3cret", " s3cret"));
        Assert.IsType<UnauthorizedResult>(Run("s3cret", "s3cre"));
        Assert.IsType<UnauthorizedResult>(Run("s3cret", "s3cretX"));
    }

    // --- conformance ------------------------------------------------------------------

    /// <summary>
    /// Every internal controller carries the guard — as a rule over the assembly, not a list.
    ///
    /// This service had no check at all on its internal routes, while ClassroomService and the
    /// Python services all required the header. The rule is written over the assembly so the next
    /// internal controller added here cannot repeat that.
    /// </summary>
    [Fact]
    public void Every_internal_route_is_guarded()
    {
        var unguarded = InternalControllers()
            .Where(c => c.GetCustomAttribute<InternalSecretAttribute>() is null)
            .Select(c => c.Name)
            .ToList();

        Assert.True(
            unguarded.Count == 0,
            "These controllers serve api/internal routes without [InternalSecret], so anything "
            + $"that can reach the port can call them: {string.Join(", ", unguarded)}");
    }

    [Fact]
    public void There_are_internal_controllers_to_check()
    {
        // Guards the guard: a reflection query that matches nothing would make the rule above
        // pass while proving nothing.
        Assert.Equal(1, InternalControllers().Count);
    }

    private static List<Type> InternalControllers()
        => typeof(InternalSecretAttribute).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetCustomAttribute<RouteAttribute>()?.Template
                ?.StartsWith("api/internal", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
}
