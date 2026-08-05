using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClassroomService.Presentation.Filters;

/// <summary>
/// Guards a service-to-service route with the shared internal secret.
///
/// Replaces a hand-rolled check that every action had to remember to call. Twenty-three actions
/// across five controllers each opened with the same line, and the day someone adds the
/// twenty-fourth and forgets it, that endpoint is simply open — with nothing to notice it.
/// Declared once on the controller, it cannot be forgotten per action.
///
/// FAILS CLOSED. The check it replaces returned "authorized" when no secret was configured, so a
/// missing or misspelled environment variable silently exposed every internal endpoint to anything
/// that could reach the port. That is the opposite of what the same guard does on the Python side
/// ("fails closed if the server has no secret configured"), and the opposite of how the rest of the
/// configuration behaves after the fail-fast work. An unconfigured guard is a broken guard, and a
/// broken guard denies.
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
