using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using StreamingService.Application.Abstractions;
using StreamingService.Presentation.Controllers;

namespace StreamingService.UnitTests;

/// <summary>
/// A session id in the URL is not permission to be in the session — `ClassroomTenancyTests`'
/// question, asked of the service that owns the live room (test-plan G-51).
///
/// The rule there keys on `classroomId`, because that is what ClassroomService scopes to. Here it is
/// `sessionId`: a method handed a session and no caller cannot tell a student in the lecture from
/// anyone else who knows the id, and this service's whole surface is addressed that way.
///
/// It is written after the fact, which is the honest order — three separate sweeps found the same
/// class of defect in this service (§7.4d's join token, and the chat, questions and broadcast group
/// below) before anybody wrote the rule that makes the next one fail at build time.
///
/// **The hub is included on purpose.** `StreamHub` is not a controller and no filter or attribute in
/// front of it ever sees the session id, so a rule that enumerated controllers would have declared
/// this service clean while `JoinStreamRoom` handed out the live feed of any lecture in the platform.
/// </summary>
public sealed class StreamingTenancyTests
{
    /// <summary>
    /// Parameter names that carry the authenticated caller.
    ///
    /// Shorter than `ClassroomTenancyTests`' list, because this service's vocabulary is smaller and
    /// the guard at the bottom of this file insists every entry is actually in use. Copying that
    /// list wholesale is how a rule acquires entries that match nothing, and an entry matching
    /// nothing is a typo waiting to be introduced silently: the day somebody names a parameter
    /// `requestingUserId` here, it should be a decision, not a coincidence that it was already
    /// accepted.
    /// </summary>
    private static readonly string[] CallerNames = ["userId", "teacherId"];

    /// <summary>
    /// Session-scoped methods that legitimately take no caller, each with the reason.
    ///
    /// Empty. Checked in both directions below, so an entry is an argument somebody has to write
    /// down rather than a parameter somebody forgot.
    /// </summary>
    private static readonly Dictionary<string, string> NoCallerNeeded = new();

    private static List<Type> BrowserControllers()
        => typeof(StreamsController).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .Where(t => !t.Name.StartsWith("Internal", StringComparison.Ordinal))
            // The LiveKit webhook is not a user surface: it is signed by LiveKit and delivered to a
            // host-published port, and LiveKitWebhookVerifierTests is its rule.
            .Where(t => !t.Name.Contains("Webhook", StringComparison.Ordinal))
            .ToList();

    /// <summary>Every SignalR hub — the surface with no controller and no attribute in front of it.</summary>
    private static List<Type> Hubs()
        => typeof(StreamsController).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.BaseType is { IsGenericType: true }
                        && t.BaseType.GetGenericTypeDefinition() == typeof(Hub<>))
            .ToList();

    /// <summary>
    /// The application-service interfaces the browser-facing surface injects, derived from its
    /// constructors rather than listed — so a new controller or hub pulls its services under the
    /// rule by existing.
    /// </summary>
    private static List<Type> SessionScopedServices()
        => BrowserControllers().Concat(Hubs())
            .SelectMany(t => t.GetConstructors())
            .SelectMany(ctor => ctor.GetParameters())
            .Select(p => p.ParameterType)
            .Where(t => t.IsInterface && t.Assembly == typeof(IInteractionService).Assembly)
            .Distinct()
            .ToList();

    private static bool TakesSessionId(MethodInfo method)
        => method.GetParameters().Any(p => p.ParameterType == typeof(Guid) && p.Name == "sessionId");

    private static bool TakesCaller(MethodInfo method)
        => method.GetParameters().Any(p => p.ParameterType == typeof(Guid) && CallerNames.Contains(p.Name));

    private static IEnumerable<(Type Service, MethodInfo Method)> SessionScopedMethods()
        => SessionScopedServices()
            .SelectMany(s => s.GetMethods().Select(m => (Service: s, Method: m)))
            .Where(x => TakesSessionId(x.Method));

    [Fact]
    public void Every_session_scoped_service_method_is_told_who_is_asking()
    {
        var anonymous = SessionScopedMethods()
            .Where(x => !TakesCaller(x.Method))
            .Select(x => $"{x.Service.Name}.{x.Method.Name}")
            .Where(name => !NoCallerNeeded.ContainsKey(name))
            .OrderBy(name => name)
            .ToList();

        Assert.True(
            anonymous.Count == 0,
            "These methods scope to a live session but never receive the caller, so knowing the "
            + "session id is the whole of the access control: " + string.Join(", ", anonymous));
    }

    [Fact]
    public void No_exemption_names_a_method_that_no_longer_exists()
    {
        var present = SessionScopedMethods()
            .Select(x => $"{x.Service.Name}.{x.Method.Name}")
            .ToHashSet();
        var stale = NoCallerNeeded.Keys.Where(name => !present.Contains(name)).ToList();

        Assert.True(stale.Count == 0, $"Exempted but no such method: {string.Join(", ", stale)}");
    }

    [Fact]
    public void No_exemption_names_a_method_that_has_since_gained_a_caller()
    {
        var unnecessary = SessionScopedMethods()
            .Where(x => NoCallerNeeded.ContainsKey($"{x.Service.Name}.{x.Method.Name}"))
            .Where(x => TakesCaller(x.Method))
            .Select(x => $"{x.Service.Name}.{x.Method.Name}")
            .ToList();

        Assert.True(
            unnecessary.Count == 0,
            $"Exempted but now takes a caller — remove from the list: {string.Join(", ", unnecessary)}");
    }

    [Fact]
    public void Every_hub_method_that_names_a_session_decides_who_may_have_it()
    {
        // The hub's own methods, not its services'. A hub method is invoked directly by a browser
        // over an open connection; there is no controller, no `[ServiceFilter]`, no model binding
        // step that could have looked at the session id. `JoinStreamRoom` took one and put the
        // connection into that session's broadcast group without asking anything at all.
        //
        // Asserted on the source, because the caller is read from `Context.User` rather than passed
        // in — there is no parameter for reflection to look for, which is exactly why the rule above
        // could not see this and why the hub needs its own.
        var source = File.ReadAllText(Path.Combine(
            ServiceRoot(), "src", "StreamingService.Presentation", "Hubs", "StreamHub.cs"));

        var hubMethods = Hubs()
            .SelectMany(h => h.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => !m.IsSpecialName)
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(Guid) && p.Name == "sessionId"))
            .ToList();

        Assert.True(hubMethods.Count >= 4, $"Only found {hubMethods.Count} session-scoped hub methods.");

        // Every one of them must either delegate to the interaction service — which now refuses a
        // stranger on every path — or check for itself. `LeaveStreamRoom` is the one that needs
        // neither: removing your own connection from a group is not something to be protected from.
        foreach (var method in hubMethods.Where(m => m.Name != "LeaveStreamRoom"))
        {
            var body = MethodBody(source, method.Name);
            Assert.True(
                body.Contains("_interactionService."),
                $"StreamHub.{method.Name} takes a session id and reaches no service that checks who "
                + "is asking, so any authenticated connection may call it for any session.");
        }
    }

    [Fact]
    public void There_are_services_hubs_and_session_scoped_methods_to_check()
    {
        // The vacuum guard. Every rule here is a reflection query, and a query matching nothing
        // passes loudly while proving nothing.
        Assert.True(BrowserControllers().Count >= 1, "No browser-facing controllers found.");
        Assert.True(Hubs().Count >= 1, "No hubs found — the surface with no attribute in front of it.");
        Assert.True(SessionScopedServices().Count >= 2, "Fewer than two session-scoped services found.");

        var methods = SessionScopedMethods().ToList();
        Assert.True(methods.Count >= 8, $"Only found {methods.Count} session-scoped methods.");

        foreach (var name in CallerNames)
        {
            Assert.True(
                methods.Any(x => x.Method.GetParameters().Any(p => p.Name == name)),
                $"No session-scoped method has a parameter named '{name}' — dead entry or a typo.");
        }
    }

    /// <summary>The text of one method, from its signature to the start of the next one.</summary>
    private static string MethodBody(string source, string name)
    {
        var start = source.IndexOf($" {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find StreamHub.{name} in the source.");

        var next = source.IndexOf("\n    public ", start + 1, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
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
