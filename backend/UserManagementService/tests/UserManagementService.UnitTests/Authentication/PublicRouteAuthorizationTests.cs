using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UserManagementService.Application.Common;
using UserManagementService.Domain.Entities;
using UserManagementService.Presentation.Controllers;

namespace UserManagementService.UnitTests.Authentication;

/// <summary>
/// Which routes need a token, and which gate stands in front of each — test-plan B-01, B-02, B-03.
///
/// ClassroomService has had this rule since §11.2 and StreamingService has the internal-secret half.
/// **UserManagementService — which owns the accounts, the roles and the whole super-admin surface —
/// had neither.** No defect follows from that today: all four controllers are gated and every
/// anonymous action is meant to be. The reason to write it down is the default.
///
/// `AuthController` carries **no class-level `[Authorize]`**. That is correct — six of its eight
/// actions must be reachable without a token, since they are how you get one — but it means the
/// controller's default is *open*, and `Logout` opts in by itself. The next action added to that
/// file is anonymous unless somebody remembers, and nothing anywhere would say so. Every other
/// controller in this service defaults the other way, which makes this the one place where the
/// habit and the default disagree.
///
/// So the anonymous surface is a list here, with a reason per entry, checked in both directions.
/// Adding a route to it is a visible edit in a security test rather than an omitted attribute.
/// </summary>
public sealed class PublicRouteAuthorizationTests
{
    /// <summary>
    /// Actions reachable with no token at all, each with the reason. Keyed on
    /// <c>Controller.Action</c>, never on the action name alone — `Refresh` or `Register` is a name
    /// another controller could take, and it would inherit the pass in silence. §11.2 learned that
    /// one from a mutation: an entry meant for one `Delete` was excusing another.
    /// </summary>
    private static readonly Dictionary<string, string> Anonymous = new()
    {
        ["AuthController.Register"] = "you cannot hold a token before you have an account",
        ["AuthController.Login"] = "this is where tokens come from",
        ["AuthController.VerifyTwoFactor"] = "stage two of a login; the first stage issued no token",
        ["AuthController.Refresh"] = "the access token is expected to be expired — that is the point",
        ["AuthController.ForgotPassword"] = "requested by someone who cannot sign in",
        ["AuthController.ResetPassword"] = "carries the emailed token instead; A-08 covers its own guards",
        ["AuthController.GetRegistrationRoles"] = "populates the role picker ON the registration form",
    };

    private static List<Type> Controllers()
        => typeof(AuthController).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .ToList();

    private static IEnumerable<MethodInfo> Actions(Type controller)
        => controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Where(m => m.GetCustomAttributes().Any(a => a.GetType().Name.StartsWith("Http", StringComparison.Ordinal)));

    /// <summary>The effective attribute for an action: its own if present, else its controller's.</summary>
    private static AuthorizeAttribute? Gate(Type controller, MethodInfo action)
        => action.GetCustomAttribute<AuthorizeAttribute>(inherit: true)
           ?? controller.GetCustomAttribute<AuthorizeAttribute>(inherit: true);

    // --- authentication ---------------------------------------------------------------

    [Fact]
    public void Every_action_requires_a_token_or_is_a_named_exception()
    {
        // Per ACTION, not per controller — because the one controller without a class-level
        // [Authorize] is the one this rule exists for, and a controller-level query would report it
        // as a single exemption and stop asking about the eight actions inside it.
        var open = Controllers()
            .SelectMany(c => Actions(c).Select(a => (Controller: c, Action: a)))
            .Where(x => Gate(x.Controller, x.Action) is null)
            .Select(x => $"{x.Controller.Name}.{x.Action.Name}")
            .Where(name => !Anonymous.ContainsKey(name))
            .OrderBy(name => name)
            .ToList();

        Assert.True(
            open.Count == 0,
            "These actions are reachable with no token and are not on the anonymous list, so anyone "
            + "on the internet may call them: " + string.Join(", ", open));
    }

    [Fact]
    public void Nothing_is_listed_as_anonymous_that_no_longer_exists()
    {
        var present = Controllers()
            .SelectMany(c => Actions(c).Select(a => $"{c.Name}.{a.Name}"))
            .ToHashSet();
        var stale = Anonymous.Keys.Where(name => !present.Contains(name)).ToList();

        Assert.True(stale.Count == 0, $"Listed as anonymous but no such action: {string.Join(", ", stale)}");
    }

    [Fact]
    public void Nothing_is_listed_as_anonymous_that_has_since_been_gated()
    {
        // The direction that lets a list rot quietly. An action that gained `[Authorize]` should
        // come off, or the list stops meaning "these are the unauthenticated ones" and starts
        // meaning "these were, once".
        var unnecessary = Controllers()
            .SelectMany(c => Actions(c).Select(a => (Controller: c, Action: a)))
            .Where(x => Anonymous.ContainsKey($"{x.Controller.Name}.{x.Action.Name}"))
            .Where(x => Gate(x.Controller, x.Action) is not null)
            .Select(x => $"{x.Controller.Name}.{x.Action.Name}")
            .ToList();

        Assert.True(
            unnecessary.Count == 0,
            $"Listed as anonymous but now gated — remove from the list: {string.Join(", ", unnecessary)}");
    }

    [Fact]
    public void Every_anonymous_action_lives_on_the_auth_controller()
    {
        // A stronger statement than the list itself, and the one worth keeping: there is exactly one
        // place in this service where the default is open, and the reason is that it is the entry
        // point. An anonymous action appearing on the admin, super-admin or users controller is not
        // an exception to be reasoned about — it is a mistake.
        var elsewhere = Anonymous.Keys
            .Where(name => !name.StartsWith("AuthController.", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            elsewhere.Count == 0,
            "Anonymous actions outside AuthController: " + string.Join(", ", elsewhere));
    }

    // --- authorization ----------------------------------------------------------------

    [Fact]
    public void The_super_admin_surface_is_gated_by_the_two_factor_policy_and_not_by_a_role()
    {
        // B-03. A role attribute would say "is a super admin"; this policy also says "proved it in
        // THIS session". The difference is the whole of §2's staged login: a stolen or long-lived
        // token from before the second factor must not reach account management, and
        // `[Authorize(Roles = "SuperAdmin")]` would accept one.
        var gate = typeof(SuperAdminController).GetCustomAttribute<AuthorizeAttribute>(inherit: true);

        Assert.NotNull(gate);
        Assert.Equal(AuthorizationPolicies.SuperAdminTwoFactor, gate!.Policy);
        Assert.Null(gate.Roles);

        // And no action inside it may weaken that by naming a role of its own, which would replace
        // the policy for that action rather than adding to it.
        var weakened = Actions(typeof(SuperAdminController))
            .Where(a => a.GetCustomAttribute<AuthorizeAttribute>(inherit: false) is not null)
            .Select(a => a.Name)
            .ToList();

        Assert.True(
            weakened.Count == 0,
            "These super-admin actions carry their own [Authorize], which overrides the policy: "
            + string.Join(", ", weakened));
    }

    [Fact]
    public void The_policy_requires_the_role_and_the_second_factor_together()
    {
        // The attribute above names a policy; this is what the policy is. Split across two files —
        // the name is in Application, the requirements are registered in Infrastructure — so
        // nothing in the type system connects them, and a policy quietly reduced to
        // `RequireRole` would leave every attribute in the service still pointing at it.
        var registration = File.ReadAllText(Path.Combine(
            ServiceRoot(), "src", "UserManagementService.Infrastructure", "DependencyInjection.cs"));

        var policy = registration[registration.IndexOf(
            "AddPolicy(AuthorizationPolicies.SuperAdminTwoFactor", StringComparison.Ordinal)..];
        policy = policy[..policy.IndexOf("});", StringComparison.Ordinal)];

        Assert.Contains($"RequireRole(RoleName.{RoleName.SuperAdmin}", policy);
        Assert.Contains("RequireClaim(TwoFactorClaims.ClaimType, TwoFactorClaims.CompletedValue)", policy);
    }

    [Fact]
    public void The_admin_surface_names_the_admin_role()
    {
        var gate = typeof(AdminController).GetCustomAttribute<AuthorizeAttribute>(inherit: true);

        Assert.NotNull(gate);
        Assert.Equal(RoleName.Admin.ToString(), gate!.Roles);
    }

    // --- guards on the guards ---------------------------------------------------------

    [Fact]
    public void There_are_controllers_and_actions_to_check()
    {
        // A reflection query that matched nothing would make every rule above pass while proving
        // nothing — the most comfortable way for a security test to become decorative. The action
        // filter is the fragile part: it looks for attributes whose name starts with "Http", so a
        // change in how routes are declared would empty it silently.
        var controllers = Controllers();
        var actions = controllers.SelectMany(Actions).ToList();

        Assert.True(controllers.Count >= 4, $"Only found {controllers.Count} controllers.");
        Assert.True(actions.Count >= 30, $"Only found {actions.Count} actions.");

        // And that the anonymous surface is genuinely reachable without a token — if `Gate` were
        // broken into always returning something, every rule above would pass vacuously.
        Assert.Contains(
            Actions(typeof(AuthController)),
            a => Gate(typeof(AuthController), a) is null);
    }

    private static string ServiceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, "src", "UserManagementService.Application")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
