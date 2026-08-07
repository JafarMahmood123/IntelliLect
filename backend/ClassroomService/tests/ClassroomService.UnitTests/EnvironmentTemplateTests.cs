using System.Text.RegularExpressions;

namespace ClassroomService.UnitTests;

/// <summary>
/// <c>backend/.env.example</c> against what the compose files actually read — test-plan M-03/M-04.
///
/// The template is the only instruction anyone gets for standing this stack up. When it drifts,
/// the failure is not a compile error or a red test: it is a new developer, or a rebuild, hitting
/// "variable is not set" on a stack that used to work, with nothing to say which value was meant
/// to go there.
///
/// Drift in the other direction is quieter and worse. A variable listed in the template but read
/// by nothing is either a leftover or the sign that a service silently stopped honouring a
/// setting — both leave someone configuring a value that does nothing.
///
/// Lives in this project because it needs no service of its own; it reads files.
///
/// **Extended in §14b to the other committed configuration**: the `appsettings*.json` files. The
/// template rule below was written for `.env.example` and stopped there, and the credential that
/// was actually in this repository — a live Gmail app password — was in an `appsettings` file the
/// rule never looked at, in a service the rule never reached.
/// </summary>
public sealed class EnvironmentTemplateTests
{
    private static readonly string BackendRoot = FindBackendRoot();

    private static string FindBackendRoot()
    {
        // Walk up from the test binary to the directory holding .env.example.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".env.example")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    /// <summary>Every <c>${VAR...}</c> reference across the compose files, defaulted or not.</summary>
    private static HashSet<string> VariablesComposeReferences()
        => ComposeVariables(@"\$\{([A-Z0-9_]+)");

    /// <summary>
    /// Only the ones a person MUST supply — <c>${VAR}</c> and <c>${VAR:?...}</c>.
    ///
    /// <c>${VAR:-default}</c> is deliberately excluded: it carries its own fallback, so leaving it
    /// out of the template is a choice rather than a hole. The e2e harness uses that form for an
    /// audio-fixture path, and demanding it in .env.example would tell everyone to configure
    /// something only the e2e suite cares about.
    /// </summary>
    private static HashSet<string> VariablesComposeRequires()
        => ComposeVariables(@"\$\{([A-Z0-9_]+)(?::-)?\}|\$\{([A-Z0-9_]+):\?");

    private static HashSet<string> ComposeVariables(string pattern)
        => Directory.EnumerateFiles(BackendRoot, "docker-compose*.yml", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), pattern)
                .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value))
            .Where(name => name.Length > 0)
            .ToHashSet();

    /// <summary>Every assignment in the template, ignoring comments and blank lines.</summary>
    private static HashSet<string> VariablesTemplateDefines()
        => File.ReadAllLines(Path.Combine(BackendRoot, ".env.example"))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && line.Contains('='))
            .Select(line => line[..line.IndexOf('=')].Trim())
            .ToHashSet();

    [Fact]
    public void Every_variable_compose_reads_is_documented_in_the_template()
    {
        var missing = VariablesComposeRequires().Except(VariablesTemplateDefines()).Order().ToList();

        Assert.True(
            missing.Count == 0,
            "These variables are required by a compose file but absent from backend/.env.example, "
            + $"so following the instructions produces a stack that will not start: {string.Join(", ", missing)}");
    }

    [Fact]
    public void The_template_documents_nothing_that_is_no_longer_read()
    {
        var unused = VariablesTemplateDefines().Except(VariablesComposeReferences()).Order().ToList();

        Assert.True(
            unused.Count == 0,
            "These variables are in backend/.env.example but no compose file reads them, so anyone "
            + $"filling them in is configuring nothing: {string.Join(", ", unused)}");
    }

    [Fact]
    public void The_template_carries_no_real_looking_secret()
    {
        // M-04. The template is committed, so anything that looks like a working credential in it
        // either IS one or teaches the next person that committing one is normal.
        var offenders = File.ReadAllLines(Path.Combine(BackendRoot, ".env.example"))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && line.Contains('='))
            .Select(line => (Key: line[..line.IndexOf('=')].Trim(), Value: line[(line.IndexOf('=') + 1)..].Trim()))
            .Where(entry => LooksLikeARealSecret(entry.Value))
            .Select(entry => entry.Key)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"These look like real values rather than placeholders: {string.Join(", ", offenders)}");
    }

    // --- §14b: the OTHER committed configuration -------------------------------------------

    /// <summary>
    /// Key names that must never carry a value in a tracked settings file, whatever it looks like.
    ///
    /// Deliberately name-based rather than value-based. <see cref="LooksLikeARealSecret"/> is an
    /// entropy heuristic, and the credential this rule was written for — a Gmail app password —
    /// is sixteen lowercase letters with no digit and no capital, so the heuristic scored it as a
    /// placeholder. A key called `AppPassword` should be empty in a committed file no matter what
    /// is in it, and that needs no guessing.
    /// </summary>
    private static readonly string[] SecretKeyNames =
        ["password", "secret", "apikey", "apisecret", "accesskey", "token", "credential"];

    /// <summary>
    /// Settings files that carry a non-empty secret-shaped key on purpose, each with the reason.
    ///
    /// Empty. Every service's committed settings blank these and read them from the environment —
    /// `Jwt:SecretKey` since §7.4, the SMTP pair since §7.6, `LiveKit:ApiKey`/`ApiSecret` since
    /// §14b. An entry here should be an argument somebody writes down.
    /// </summary>
    private static readonly Dictionary<string, string> SecretsAllowedInSettings = new();

    [Fact]
    public void No_tracked_settings_file_carries_a_secret()
    {
        var offenders = TrackedSettingsFiles()
            .SelectMany(file => SecretShapedEntries(file).Select(entry => $"{Relative(file)}:{entry}"))
            .Where(entry => !SecretsAllowedInSettings.ContainsKey(entry))
            .OrderBy(entry => entry)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These committed settings carry a value under a secret-shaped key. Blank it and read it "
            + "from the environment, as every other service does: " + string.Join(", ", offenders));
    }

    [Fact]
    public void No_exemption_names_a_setting_that_is_no_longer_there()
    {
        var present = TrackedSettingsFiles()
            .SelectMany(file => SecretShapedEntries(file).Select(entry => $"{Relative(file)}:{entry}"))
            .ToHashSet();
        var stale = SecretsAllowedInSettings.Keys.Where(key => !present.Contains(key)).ToList();

        Assert.True(stale.Count == 0, $"Exempted but no such setting: {string.Join(", ", stale)}");
    }

    [Fact]
    public void No_development_settings_file_is_tracked()
    {
        // `.gitignore` has said `appsettings.Development.json` since long before §14b, and
        // UserManagementService's copy was tracked anyway — ignoring a path does not untrack a file
        // that was committed before the rule existed. That file held the live Gmail app password.
        //
        // These are personal local-run files: four services have one on disk and none of them
        // should be in the repository.
        var tracked = TrackedFiles()
            .Where(path => Path.GetFileName(path)
                .Equals("appsettings.Development.json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            tracked.Count == 0,
            "These are local development files and are tracked, so whatever is in them is in the "
            + "repository: " + string.Join(", ", tracked));
    }

    [Fact]
    public void There_are_tracked_settings_files_to_check()
    {
        // The vacuum guard, and it matters more than usual here: every rule above is driven by
        // `git ls-files`, so a git failure or a changed working directory would report an empty
        // list and pass all three while reading nothing.
        var files = TrackedSettingsFiles();
        Assert.True(files.Count >= 4, $"Only found {files.Count} tracked settings files.");

        // And that the secret-shaped detector can actually fire, which no passing run proves.
        Assert.NotEmpty(SecretShapedEntries(
            WriteTemp("{ \"Smtp\": { \"AppPassword\": \"abcdefghijklmnop\" } }")));
        Assert.Empty(SecretShapedEntries(WriteTemp("{ \"Smtp\": { \"AppPassword\": \"\" } }")));
        Assert.Empty(SecretShapedEntries(WriteTemp("{ \"Smtp\": { \"SenderEmail\": \"a@b.c\" } }")));
    }

    private static string WriteTemp(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>`Section.Key` for every non-empty value under a secret-shaped key name.</summary>
    private static List<string> SecretShapedEntries(string file)
    {
        var found = new List<string>();
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
        Walk(document.RootElement, string.Empty, found);
        return found;
    }

    private static void Walk(System.Text.Json.JsonElement element, string prefix, List<string> found)
    {
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object) return;

        foreach (var property in element.EnumerateObject())
        {
            var path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";

            if (property.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                Walk(property.Value, path, found);
                continue;
            }

            if (property.Value.ValueKind != System.Text.Json.JsonValueKind.String) continue;

            var isSecretShaped = SecretKeyNames.Any(
                name => property.Name.Replace("_", string.Empty)
                    .Contains(name, StringComparison.OrdinalIgnoreCase));

            if (isSecretShaped && !string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                found.Add(path);
            }
        }
    }

    private static List<string> TrackedSettingsFiles()
        => TrackedFiles()
            .Where(path => Path.GetFileName(path).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
            .Where(path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.Combine(RepoRoot(), path))
            .Where(File.Exists)
            .ToList();

    /// <summary>Every path git tracks. Fails loudly rather than returning nothing.</summary>
    private static List<string> TrackedFiles()
    {
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = "ls-files",
            WorkingDirectory = RepoRoot(),
            RedirectStandardOutput = true,
        });

        Assert.NotNull(process);
        var output = process!.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, "`git ls-files` failed; this rule cannot run without it.");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
    }

    private static string RepoRoot() => new DirectoryInfo(BackendRoot).Parent!.FullName;

    private static string Relative(string path)
        => Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/');

    /// <summary>
    /// A deliberately blunt heuristic: long, high-entropy-looking and not obviously a placeholder.
    /// It is meant to catch a pasted credential, not to grade password strength.
    ///
    /// **It missed the one credential this repository actually had.** A Gmail app password is
    /// sixteen lowercase letters — no digit, no capital — so it scored as a placeholder. That is
    /// why the settings rule above is keyed on the NAME of the setting instead. This one still
    /// earns its place on `.env.example`, where the keys are not ours to predict.
    /// </summary>
    private static bool LooksLikeARealSecret(string value)
    {
        if (value.Length < 16) return false;

        var placeholderWords = new[] { "change", "example", "placeholder", "your", "replace", "todo", "xxx", "dev" };
        if (placeholderWords.Any(word => value.Contains(word, StringComparison.OrdinalIgnoreCase))) return false;

        // A run of mixed-case alphanumerics with no separators is what a generated secret looks
        // like; a human-written placeholder almost always has a dash, underscore or space.
        return !value.Any(c => c is '-' or '_' or ' ' or '.')
            && value.Any(char.IsDigit)
            && value.Any(char.IsUpper)
            && value.Any(char.IsLower);
    }
}
