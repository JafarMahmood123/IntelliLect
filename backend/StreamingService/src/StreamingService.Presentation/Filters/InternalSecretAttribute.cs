using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace StreamingService.Presentation.Filters;

/// <summary>
/// Guards a service-to-service route with the shared internal secret.
///
/// This service's internal controller had NO check at all, while ClassroomService and the Python
/// services all required the header. Being off the nginx path made it unreachable from outside,
/// which is a network fact rather than a guarantee: anything that lands on the internal docker
/// network — another compromised container, a misconfigured port publish — could end a live
/// session or drive an egress. Defence in depth means the network is not the only thing standing
/// there.
///
/// FAILS CLOSED. An unconfigured secret denies rather than allows, matching the Python guard
/// ("fails closed if the server has no secret configured"). A guard that switches itself off when
/// misconfigured is worse than no guard, because it is believed in.
///
/// Runs as an authorization filter, so it rejects before model binding — an unauthenticated caller
/// never gets a request body deserialized on their behalf.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class InternalSecretAttribute : Attribute, IAuthorizationFilter
{
    public const string HeaderName = "X-Internal-Secret";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var expected = context.HttpContext.RequestServices
            .GetRequiredService<IConfiguration>()["Internal:ApiSecret"];

        if (string.IsNullOrWhiteSpace(expected))
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var provided = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (!SecretsMatch(provided, expected))
        {
            context.Result = new UnauthorizedResult();
        }
    }

    /// <summary>
    /// Constant-time comparison. An ordinary string equality returns as soon as two bytes differ,
    /// which leaks how much of the secret a caller has guessed — cheap to avoid, and this is a
    /// long-lived credential shared by every service.
    /// </summary>
    private static bool SecretsMatch(string provided, string expected)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);

        // FixedTimeEquals requires equal lengths; comparing the lengths first leaks only the
        // length, which is not the secret.
        return providedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
