using System.Text.RegularExpressions;

namespace ClassroomService.UnitTests;

/// <summary>
/// Every `Section__Key` the compose files supply is read by something (test-plan Q-14).
///
/// Q-01 does this for the Python services and it is the highest-value rule in the suite: it found
/// `RAG_BASE_URL` bound to nothing after a rename, which meant the live assistant had never
/// retrieved a word of course material in the deployed stack and every idea degraded to "no
/// feedback" — with nothing failing anywhere.
///
/// **.NET's binder is exactly as quiet.** An environment variable naming a section or property
/// that does not exist is ignored in silence: the options object keeps its default, the service
/// starts, and the setting somebody carefully put in compose does nothing. This service has
/// already had one — StreamingService's internal-client timeout was hard-coded to 5s while
/// compose supplied a `TimeoutSeconds` that nothing read, so the value in the file was a
/// suggestion and changing it changed nothing.
///
/// The row said "not covered — a static rule gave false positives on every service", and that was
/// true of the obvious rule. .NET reaches a setting three different ways, and a rule that knows
/// only one flags the other two:
///
///   * `configuration["Section:Key"]` — one string, easy;
///   * `GetSection("Section")` then `settings["Key"]` — two steps, in two places;
///   * an options class with `SectionName = "Section"` whose PROPERTY is named `Key` — and for
///     `Egress__S3__AccessKey`, a property named `S3` whose type declares `AccessKey`.
///
/// So this walks the prefixes: `A__B__C` is read if anything binds `A` and names both `B` and
/// `C`, or binds `A:B` and names `C`, or indexes the whole path directly. Across the 54 settings
/// the compose files pass today, that reports zero — which is what makes it usable as a rule
/// rather than a list of exceptions.
/// </summary>
public sealed class DotNetSettingsBindingTests
{
    private static readonly string BackendRoot = FindBackendRoot();

    /// <summary>What one C# file binds and what it names, which is all the rule needs.</summary>
    private sealed record SourceFile(string Path, HashSet<string> Sections, HashSet<string> Names);

    /// <summary>
    /// Compose service name to the source folder that answers for it.
    ///
    /// Attribution is the difference between this rule working and merely appearing to. Asking
    /// "does ANYTHING read this?" is satisfied by any service that happens to declare the same
    /// section and property — and two of them do: both UserManagementService and ClassroomService
    /// bind a `StreamingService` section with a `TimeoutSeconds`. A mutation that orphaned UMS's
    /// copy sailed through the global version of this rule, which is exactly the defect the row
    /// exists for.
    ///
    /// The Python services are absent on purpose: Q-01 already covers them, and it can do better
    /// because pydantic-settings knows its own field list.
    /// </summary>
    private static readonly Dictionary<string, string> ServiceFolder = new()
    {
        ["user-service"] = "UserManagementService",
        ["classroom-service"] = "ClassroomService",
        ["streaming-service"] = "StreamingService",
        ["email-service"] = "EmailService",
    };

    private static readonly List<SourceFile> Sources = ReadSources();
    private static readonly List<(string Service, string Setting)> ComposeSettings = ReadComposeSettings();

    [Fact]
    public void Every_setting_compose_supplies_is_read_by_something()
    {
        var ignored = ComposeSettings
            .Where(entry => !IsRead(entry.Setting, entry.Service))
            .Select(entry => $"{entry.Setting} (passed to {entry.Service})")
            .ToList();

        Assert.True(
            ignored.Count == 0,
            "These are passed to a .NET service and bind to nothing. The binder ignores them in "
            + "silence, so the service starts, keeps its default, and the value in the compose "
            + "file is a suggestion: " + string.Join(", ", ignored));
    }

    [Fact]
    public void There_are_settings_and_sources_to_check()
    {
        // Both sides are read off disk, so either could quietly become empty — a renamed compose
        // file, a moved service folder — and take the rule green with it.
        Assert.True(ComposeSettings.Count >= 40, $"only {ComposeSettings.Count} settings found");
        // Every .NET service must actually appear, or a renamed compose block would silently
        // drop that service's settings out of the rule instead of failing it.
        foreach (var service in ServiceFolder.Keys)
        {
            Assert.Contains(ComposeSettings, entry => entry.Service == service);
        }
        Assert.True(Sources.Count >= 200, $"only {Sources.Count} C# files found");
    }

    [Theory]
    [InlineData("Jwt__SecretKey", "user-service")]
    [InlineData("S3Settings__ServiceUrl", "classroom-service")]
    [InlineData("Egress__S3__AccessKey", "streaming-service")]
    [InlineData("EmailSettings__SenderEmail", "email-service")]
    public void The_three_ways_dotnet_reaches_a_setting_are_all_recognised(string setting, string service)
    {
        // One real example of each shape, named rather than left implicit — because the reason
        // this row sat at "not covered" is that a rule knowing only the first shape reports the
        // other two as defects, and a rule that cries wolf on a correct configuration is a rule
        // somebody deletes. `Egress__S3__AccessKey` is the nested case: a property named S3 whose
        // type declares AccessKey; `EmailSettings__SenderEmail` is GetSection then an indexer.
        Assert.True(IsRead(setting, service), $"{setting} is read, but the rule cannot see it");
    }

    [Fact]
    public void A_setting_that_matches_nothing_is_reported()
    {
        // The other direction. Without this the rule could be broken into always returning true —
        // by a regex that matches everything, say — and would then pass forever over any input.
        Assert.False(IsRead("Nonsense__NotAThing", "user-service"));
        Assert.False(IsRead("Jwt__NotAThing", "user-service"));
        // And the attribution itself: ClassroomService binds a StreamingService section with a
        // TimeoutSeconds, so this is read by SOMETHING — just not by the service it is passed to.
        Assert.False(IsRead("S3Settings__ServiceUrl", "email-service"));
    }

    // --- the rule ---------------------------------------------------------------------------

    /// <summary>
    /// Whether anything reads `Section__Key`, allowing for the section boundary sitting anywhere
    /// along the path — `A__B__C` may be section `A` with a nested `B.C`, or section `A:B` with a
    /// key `C`.
    /// </summary>
    private static bool IsRead(string setting, string service)
    {
        var parts = setting.Split("__");
        var folder = Path.Combine(BackendRoot, ServiceFolder[service]) + Path.DirectorySeparatorChar;
        var sources = Sources.Where(file => file.Path.StartsWith(folder, StringComparison.Ordinal)).ToList();

        // Shape 1: configuration["A:B:C"] spelled out in one string.
        if (sources.Any(file => file.Names.Contains(string.Join(":", parts))))
        {
            return true;
        }

        for (var split = 1; split < parts.Length; split++)
        {
            var section = string.Join(":", parts.Take(split));
            var rest = parts.Skip(split).ToArray();

            // Shapes 2 and 3: a file that binds the section and names every remaining segment,
            // whether as an indexer key or as a property.
            if (sources.Any(file => file.Sections.Contains(section) && rest.All(file.Names.Contains)))
            {
                return true;
            }
        }

        return false;
    }

    private static List<SourceFile> ReadSources()
    {
        var files = new List<SourceFile>();
        foreach (var path in Directory.EnumerateFiles(BackendRoot, "*.cs", SearchOption.AllDirectories))
        {
            // Production source only. A test file that mentions a setting — an in-memory
            // configuration dictionary, say — otherwise satisfies this rule on the service's
            // behalf, so a property could be renamed out of the options class and the rule would
            // keep passing because the TEST still names it. That is not hypothetical: it is what
            // hid the mutation this rule was written to catch, on the first attempt.
            if (path.Contains("/obj/", StringComparison.Ordinal)
                || path.Contains("/bin/", StringComparison.Ordinal)
                || path.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var source = File.ReadAllText(path);
            var sections = Regex.Matches(source, @"GetSection\(""([^""]+)""\)")
                .Select(m => m.Groups[1].Value)
                .Concat(Regex.Matches(source, @"SectionName\s*=\s*""([^""]+)""").Select(m => m.Groups[1].Value))
                // GetConnectionString("X") is ConnectionStrings:X by another name — a dedicated
                // API rather than a section lookup, and invisible to a rule that only knows the
                // general one.
                .Concat(Regex.IsMatch(source, @"GetConnectionString\(")
                    ? new[] { "ConnectionStrings" }
                    : Array.Empty<string>())
                .ToHashSet(StringComparer.Ordinal);
            // ANY string literal that looks like a settings path, not only indexer syntax: the
            // composition roots read required values through a helper — `Required(configuration,
            // "RabbitMq:Username")` — so the path is an argument rather than a subscript.
            var names = Regex.Matches(source, @"""([A-Za-z0-9_]+(?::[A-Za-z0-9_]+)*)""")
                .Select(m => m.Groups[1].Value)
                .Concat(Regex.Matches(source, @"\b([A-Z][A-Za-z0-9]*)\s*\{\s*get").Select(m => m.Groups[1].Value))
                .ToHashSet(StringComparer.Ordinal);

            files.Add(new SourceFile(path, sections, names));
        }
        return files;
    }

    /// <summary>
    /// Every `Section__Key` environment variable the compose files set.
    ///
    /// The double underscore is .NET's own section separator, so this selects exactly the
    /// variables that are meant to bind — and ignores the plain ones (PATH, ASPNETCORE_*, and the
    /// Python services' flat settings), which have their own rules.
    /// </summary>
    private static List<(string Service, string Setting)> ReadComposeSettings()
    {
        var settings = new HashSet<(string, string)>();
        foreach (var compose in Directory.EnumerateFiles(BackendRoot, "docker-compose*.yml", SearchOption.AllDirectories))
        {
            if (compose.Contains("/obj/", StringComparison.Ordinal)
                || compose.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            // Two-space-indented keys are the service blocks; everything below one belongs to it
            // until the next. Enough YAML for this file shape, and it fails loudly rather than
            // quietly if that shape changes, because the count guard below stops matching.
            var current = string.Empty;
            foreach (var line in File.ReadAllLines(compose))
            {
                var block = Regex.Match(line, @"^  ([a-z][a-z0-9-]*):\s*$");
                if (block.Success)
                {
                    current = block.Groups[1].Value;
                    continue;
                }

                var setting = Regex.Match(line, @"^\s+-\s+([A-Za-z][A-Za-z0-9]*(?:__[A-Za-z0-9_]+)+)=");
                if (setting.Success && ServiceFolder.ContainsKey(current))
                {
                    settings.Add((current, setting.Groups[1].Value));
                }
            }
        }
        return settings.OrderBy(entry => entry.Item1, StringComparer.Ordinal)
            .ThenBy(entry => entry.Item2, StringComparer.Ordinal)
            .Select(entry => (Service: entry.Item1, Setting: entry.Item2))
            .ToList();
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
