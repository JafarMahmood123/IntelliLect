using System.Text.RegularExpressions;

namespace ClassroomService.UnitTests;

/// <summary>
/// What nginx's route table actually exposes (test-plan B-10).
///
/// B-08 and B-09 prove that an `/api/internal` route refuses a caller without the shared secret.
/// B-10 is the layer in front: those routes should not be reachable from outside at all, so the
/// secret is the second lock rather than the only one. Its existing coverage is
/// `tests/e2e/test_internal_surface_contract.py`, and that suite's own note says it probes
/// **in-network** — so it tests the guard, deliberately, and **nothing tests the route table.**
///
/// The route table is where the interesting failure lives, because of one line:
///
///     location /api/ { proxy_pass http://user-service-proxy/api/; }
///
/// That is a prefix catch-all. Everything under `/api/` that no more specific location claims
/// goes to UserManagementService. Today that is safe, and safe **by accident of placement**: all
/// six internal controllers happen to live in ClassroomService and StreamingService, which are
/// reached only through `/api/classrooms` and `/api/streams`. An internal controller added to
/// UserManagementService — one file, no other change — is public the moment it compiles, with
/// nothing in this repository that would notice.
///
/// So this reads `nginx.conf` and every controller's `[Route]`, resolves each route the way nginx
/// resolves it, and asks where it would land. It is the same argument as E-07: both artefacts are
/// files this repository owns, and reading them proves the property for every deployment built
/// from them rather than for one running stack.
/// </summary>
public sealed class NginxRouteTableTests
{
    private static readonly string BackendRoot = FindBackendRoot();

    /// <summary>Compose hostname per service directory, taken from the upstreams nginx declares.</summary>
    private static readonly Dictionary<string, string> HostnameByService = new()
    {
        ["UserManagementService"] = "user-management-service",
        ["ClassroomService"] = "classroom-service",
        ["StreamingService"] = "streaming-service",
    };

    /// <summary>
    /// Public routes that are deliberately NOT reachable through the gateway, each with the
    /// reason. Checked in both directions below, so an entry that stops being true fails.
    /// </summary>
    private static readonly Dictionary<string, string> NotThroughTheGateway = new()
    {
        ["api/webhooks/livekit"] =
            "livekit-server runs with host networking and delivers its egress/room webhooks to a "
            + "host-published port (8085 -> 8080), never through nginx. See the note in "
            + "StreamingService/docker-compose.unit.yml.",
    };

    // --- the rule B-10 is about ------------------------------------------------------------

    [Fact]
    public void No_internal_route_is_reachable_through_the_gateway()
    {
        var exposed = InternalRoutes()
            .Where(route => UpstreamFor(route.Path) == HostnameByService[route.Service])
            .Select(route => $"{route.Path} (hosted by {route.Service})")
            .ToList();

        Assert.True(
            exposed.Count == 0,
            "These internal routes are proxied from outside to the service that hosts them, so "
            + "the shared secret is the only thing between the public internet and them: "
            + string.Join("; ", exposed));
    }

    [Fact]
    public void An_internal_route_added_to_the_catch_all_service_would_be_caught()
    {
        // The hazard this rule exists for, exercised rather than described. `location /api/` sends
        // everything unclaimed to UserManagementService, so an internal controller added there is
        // public immediately — and the rule above would pass today with or without this check,
        // because no such controller exists yet. This proves the rule can see one.
        Assert.Equal(
            HostnameByService["UserManagementService"],
            UpstreamFor("api/internal/anything-at-all"));
    }

    [Fact]
    public void Every_internal_route_still_lands_on_a_service_that_does_not_host_it()
    {
        // The other half of the same fact, and the one that explains WHY the system is currently
        // safe: these paths do resolve — to UserManagementService, which has no such route and
        // answers 404. Safe, but by placement rather than by policy, which is what makes the rule
        // above worth keeping.
        foreach (var route in InternalRoutes())
        {
            Assert.NotEqual(HostnameByService[route.Service], UpstreamFor(route.Path));
        }
    }

    // --- the converse: a public route must actually reach its own service -------------------

    [Fact]
    public void Every_public_route_reaches_the_service_that_hosts_it()
    {
        // A misrouted public route fails in a way nobody attributes to nginx: the browser gets a
        // 404 from a service that was never meant to answer, and the service that owns the
        // endpoint logs nothing at all.
        var misrouted = PublicRoutes()
            .Where(route => !NotThroughTheGateway.ContainsKey(route.Path))
            .Where(route => UpstreamFor(route.Path) != HostnameByService[route.Service])
            .Select(route => $"{route.Path} is hosted by {route.Service} but nginx sends it to "
                             + $"{UpstreamFor(route.Path) ?? "nothing"}")
            .ToList();

        Assert.True(misrouted.Count == 0, string.Join("; ", misrouted));
    }

    [Fact]
    public void Every_exemption_is_still_a_route_that_exists_and_is_still_unreachable()
    {
        // Both directions on the exemption list. An entry naming a deleted controller is a
        // comment nobody will ever check; an entry whose route has since been wired through the
        // gateway is worse, because it claims the opposite of what is true.
        var allRoutes = PublicRoutes().Select(r => r.Path).ToHashSet();

        foreach (var (path, reason) in NotThroughTheGateway)
        {
            Assert.True(allRoutes.Contains(path), $"exempted route {path} no longer exists");
            Assert.False(string.IsNullOrWhiteSpace(reason));

            var route = PublicRoutes().First(r => r.Path == path);
            Assert.NotEqual(HostnameByService[route.Service], UpstreamFor(path));
        }
    }

    // --- guards on the reading itself -------------------------------------------------------

    [Fact]
    public void The_route_table_and_the_controllers_were_both_actually_read()
    {
        // Every rule above passes over an empty set. Both sides are read from files, so either
        // could quietly become empty — a moved config, a renamed folder, a changed attribute
        // style — and take the whole suite green with it.
        Assert.True(Locations().Count >= 4, "nginx.conf yielded almost no locations");
        Assert.True(InternalRoutes().Count >= 6, "found fewer internal controllers than exist");
        Assert.True(PublicRoutes().Count >= 10, "found fewer public controllers than exist");
    }

    [Theory]
    [InlineData("api/classrooms", "classroom-service")]
    [InlineData("api/classrooms/x/files", "classroom-service")]
    [InlineData("api/streams", "streaming-service")]
    [InlineData("api/auth/login", "user-management-service")]
    [InlineData("api/internal/classrooms", "user-management-service")]
    [InlineData("nothing/matches/this", null)]
    public void Paths_resolve_the_way_nginx_resolves_them(string path, string? expected)
    {
        // nginx picks the LONGEST matching prefix, not the first one written. Resolving by first
        // match would send /api/classrooms to user-management-service and make every rule above
        // report the opposite of the truth.
        Assert.Equal(expected, UpstreamFor(path));
    }

    // --- helpers -----------------------------------------------------------------------------

    private sealed record ControllerRoute(string Service, string Path);

    /// <summary>Longest-prefix match over nginx's locations, then the upstream's server host.</summary>
    private static string? UpstreamFor(string path)
    {
        var candidate = "/" + path.TrimStart('/');
        var best = Locations()
            .Where(location => candidate.StartsWith(location.Key, StringComparison.Ordinal))
            .OrderByDescending(location => location.Key.Length)
            .Select(location => location.Value)
            .FirstOrDefault();

        return best is null ? null : Upstreams().GetValueOrDefault(best);
    }

    private static Dictionary<string, string> Locations()
    {
        var config = File.ReadAllText(Path.Combine(BackendRoot, "nginx.conf"));
        var found = new Dictionary<string, string>();
        foreach (Match match in Regex.Matches(
            config, @"location\s+([^\s{]+)\s*\{(.*?)\n\s*\}", RegexOptions.Singleline))
        {
            var proxy = Regex.Match(match.Groups[2].Value, @"proxy_pass\s+https?://([A-Za-z0-9_.-]+)");
            if (proxy.Success)
            {
                found[match.Groups[1].Value] = proxy.Groups[1].Value;
            }
        }
        return found;
    }

    private static Dictionary<string, string> Upstreams()
    {
        var config = File.ReadAllText(Path.Combine(BackendRoot, "nginx.conf"));
        return Regex.Matches(config, @"upstream\s+([A-Za-z0-9_.-]+)\s*\{\s*server\s+([A-Za-z0-9_.-]+):")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    }

    private static List<ControllerRoute> InternalRoutes()
        => AllRoutes().Where(r => r.Path.StartsWith("api/internal", StringComparison.Ordinal)).ToList();

    private static List<ControllerRoute> PublicRoutes()
        => AllRoutes().Where(r => !r.Path.StartsWith("api/internal", StringComparison.Ordinal)).ToList();

    /// <summary>
    /// Every controller's route attribute, read from source across all three services.
    ///
    /// Source rather than reflection: this test project references ClassroomService only, and
    /// loading the other two would make a rule about the whole platform depend on which
    /// assemblies happened to be in the bin folder.
    /// </summary>
    private static List<ControllerRoute> AllRoutes()
    {
        var routes = new List<ControllerRoute>();
        foreach (var (service, _) in HostnameByService)
        {
            var controllers = Path.Combine(
                BackendRoot, service, "src", $"{service}.Presentation", "Controllers");
            if (!Directory.Exists(controllers))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(controllers, "*.cs", SearchOption.AllDirectories))
            {
                var match = Regex.Match(File.ReadAllText(file), @"\[Route\(""([^""]+)""\)\]");
                if (match.Success)
                {
                    routes.Add(new ControllerRoute(service, match.Groups[1].Value));
                }
            }
        }
        return routes;
    }

    private static string FindBackendRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".env.example")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
