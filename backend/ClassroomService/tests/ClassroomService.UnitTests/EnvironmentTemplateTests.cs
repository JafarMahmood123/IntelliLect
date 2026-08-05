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

    /// <summary>
    /// A deliberately blunt heuristic: long, high-entropy-looking and not obviously a placeholder.
    /// It is meant to catch a pasted credential, not to grade password strength.
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
