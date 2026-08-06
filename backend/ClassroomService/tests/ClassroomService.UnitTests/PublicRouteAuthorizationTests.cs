using System.Reflection;
using ClassroomService.Presentation.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClassroomService.UnitTests;

/// <summary>
/// Every public route requires authentication, and every state-changing one names a role — as
/// rules over the assembly rather than a list somebody maintains. Test-plan B-01/B-02.
///
/// Area B is the largest hole in the suite for a structural reason: the services are tested and
/// the *guards in front of them* are not. Proving a 401 or a 403 properly needs a running host,
/// which is integration work. But the failure that actually happens is not ASP.NET forgetting to
/// enforce `[Authorize]` — it is a new endpoint shipping without one, and that is a question
/// about the assembly, answerable here, today, with no container.
///
/// The same shape as <see cref="InternalSecretGuardTests"/>, and for the same reason: the check
/// used to be a habit repeated per controller, and nothing anywhere would notice the one that
/// forgot.
/// </summary>
public sealed class PublicRouteAuthorizationTests
{
    /// <summary>
    /// Controllers that are deliberately reachable without a user token, each with the reason.
    /// Being on this list is a decision, which is the point of it being a list: adding a
    /// controller here is a visible edit in a security test, not an omitted attribute.
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new()
    {
        // Guarded by [InternalSecret] instead — a shared secret, no user token involved. Covered
        // by InternalSecretGuardTests, including its own rule over the assembly.
        ["InternalClassroomsController"] = "internal surface, secret-guarded",
        ["InternalFilesController"] = "internal surface, secret-guarded",
        ["InternalOutputsController"] = "internal surface, secret-guarded",
        ["InternalSessionsController"] = "internal surface, secret-guarded",
        ["InternalUsersController"] = "internal surface, secret-guarded",
    };

    private static List<Type> Controllers()
        => typeof(InternalSecretAttribute).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .ToList();

    private static bool RequiresAuthentication(Type controller)
        => controller.GetCustomAttribute<AuthorizeAttribute>(inherit: true) is not null;

    /// <summary>The HTTP verbs that change something, and therefore need a role.</summary>
    private static readonly Type[] MutatingVerbs =
    [
        typeof(HttpPostAttribute), typeof(HttpPutAttribute),
        typeof(HttpDeleteAttribute), typeof(HttpPatchAttribute),
    ];

    private static IEnumerable<MethodInfo> Actions(Type controller)
        => controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName);

    private static bool IsMutating(MethodInfo action)
        => MutatingVerbs.Any(verb => action.GetCustomAttributes(verb, inherit: true).Length > 0);

    // --- authentication -----------------------------------------------------------

    [Fact]
    public void Every_controller_either_requires_authentication_or_is_a_named_exception()
    {
        var unguarded = Controllers()
            .Where(c => !RequiresAuthentication(c))
            .Select(c => c.Name)
            .Where(name => !Exempt.ContainsKey(name))
            .ToList();

        Assert.True(
            unguarded.Count == 0,
            "These controllers serve routes with no [Authorize] and are not on the exemption "
            + $"list, so anyone can call them: {string.Join(", ", unguarded)}");
    }

    [Fact]
    public void Nobody_is_exempt_who_no_longer_exists()
    {
        // An exemption for a deleted controller is dead permission: it says a name is allowed
        // through, and the next controller to take that name inherits the pass silently.
        var present = Controllers().Select(c => c.Name).ToHashSet();
        var stale = Exempt.Keys.Where(name => !present.Contains(name)).ToList();

        Assert.True(stale.Count == 0, $"Exempted but no longer present: {string.Join(", ", stale)}");
    }

    [Fact]
    public void Nobody_is_exempt_who_does_not_need_to_be()
    {
        // The other direction, and the one that lets the list rot quietly: a controller that
        // gained [Authorize] should come OFF the list, or the list stops meaning "these are the
        // unauthenticated ones".
        var unnecessary = Controllers()
            .Where(c => Exempt.ContainsKey(c.Name) && RequiresAuthentication(c))
            .Select(c => c.Name)
            .ToList();

        Assert.True(
            unnecessary.Count == 0,
            $"Exempted but now authenticated — remove from the list: {string.Join(", ", unnecessary)}");
    }

    // --- authorization ------------------------------------------------------------

    /// <summary>
    /// State-changing actions whose authorization is decided in the SERVICE layer, each with the
    /// reason a role attribute would be wrong rather than merely absent.
    ///
    /// Writing this rule as "every mutation names a role" was the obvious first shape, and it was
    /// the wrong one — it flagged three actions that are all correct. The real invariant is that
    /// every mutation's authorization is decided *somewhere explicit*; a role attribute is one way
    /// and the only one visible by reflection, so the others are listed here on purpose.
    /// </summary>
    /// Keyed on <c>Controller.Action</c>, not on the action name: `Delete` alone would have
    /// excused <c>ClassroomsController.Delete</c> too, which is genuinely role-gated. Mutation
    /// testing caught exactly that — removing its `[Authorize(Roles = "Teacher")]` changed
    /// nothing, because an entry meant for recordings was silently covering it.
    private static readonly Dictionary<string, string> ServiceLayerAuthorized = new()
    {
        // A role cannot express these two: "Student" does not prove enrolment in THIS classroom.
        // QuizService.EnsureEnrolledStudentAsync does, and also refuses the teacher — who is a
        // member of their own classroom but not a participant in their own quiz.
        ["QuizzesController.SubmitAnswer"] = "enrolment in this classroom, checked in QuizService",
        ["QuizzesController.SubmitQuiz"] = "enrolment in this classroom, checked in QuizService",
        // POST only because it carries a question body; it reads. Teachers AND students may ask,
        // so no single role fits. ClassroomQaService.EnsureMemberAsync enforces membership.
        ["QaController.Answer"] = "membership, checked in ClassroomQaService",
        // Deliberately NOT [Authorize(Roles = "Teacher")]: that would lock out Admins, who are
        // explicitly allowed. The controller passes User.IsInRole("Admin") into
        // RecordingLifecycleService.EnsureTeacherOrAdminAsync, which decides on role AND ownership.
        ["RecordingsController.Delete"] = "teacher-or-admin plus ownership, checked in RecordingLifecycleService",
    };

    [Fact]
    public void Every_state_changing_action_decides_authorization_somewhere_explicit()
    {
        // A GET can be left to a membership check in the service layer — a student reading their
        // own classroom's files is legitimate, and only the service knows whether they are
        // enrolled. A POST/PUT/DELETE is different: "who may do this at all" has to be answered,
        // and an action that answers it nowhere is how "students can delete classroom materials"
        // ships.
        var undecided = Controllers()
            .Where(c => !Exempt.ContainsKey(c.Name))
            .SelectMany(c => Actions(c).Select(a => (Controller: c, Action: a)))
            .Where(x => IsMutating(x.Action))
            .Where(x => !ServiceLayerAuthorized.ContainsKey($"{x.Controller.Name}.{x.Action.Name}"))
            .Where(x => x.Action.GetCustomAttribute<AuthorizeAttribute>(inherit: true)?.Roles is null)
            .Select(x => $"{x.Controller.Name}.{x.Action.Name}")
            .ToList();

        Assert.True(
            undecided.Count == 0,
            "These actions change state, name no role, and are not on the service-layer list — so "
            + "any authenticated user may call them, and nothing says that was intended: "
            + string.Join(", ", undecided));
    }

    [Fact]
    public void Every_service_layer_exception_still_refers_to_a_real_action()
    {
        // A renamed or deleted action leaves an entry that excuses nothing — or worse, excuses
        // whatever later takes the name. `Delete` in particular is a name another controller
        // could easily reuse, and it would inherit this pass in silence.
        var names = Controllers()
            .SelectMany(c => Actions(c).Select(a => $"{c.Name}.{a.Name}"))
            .ToHashSet();
        var stale = ServiceLayerAuthorized.Keys.Where(name => !names.Contains(name)).ToList();

        Assert.True(stale.Count == 0, $"Listed but no such action: {string.Join(", ", stale)}");
    }

    // --- guards on the guards -----------------------------------------------------

    [Fact]
    public void There_are_controllers_and_mutating_actions_to_check()
    {
        // A reflection query that matched nothing would make every rule above pass while proving
        // nothing at all — the most comfortable way for a security test to become decorative.
        var controllers = Controllers();
        var mutating = controllers
            .Where(c => !Exempt.ContainsKey(c.Name))
            .SelectMany(Actions)
            .Count(IsMutating);

        Assert.True(controllers.Count >= 10, $"Only found {controllers.Count} controllers.");
        Assert.True(mutating >= 15, $"Only found {mutating} state-changing actions.");
    }
}
