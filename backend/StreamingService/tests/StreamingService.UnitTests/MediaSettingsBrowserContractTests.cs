using System.Globalization;
using System.Text.RegularExpressions;
using StreamingService.Application.DTOs;
using StreamingService.Infrastructure.Configuration;

namespace StreamingService.UnitTests;

/// <summary>
/// The media settings actually reach the browser, and mean the same thing when they get there
/// (work-plan P1).
///
/// P1 is parked on "needs running containers" and its first item is *confirm the `media` settings
/// object reaches the browser*. Two thirds of that needs no container. `MediaOptionsTests` pins how
/// the section binds and `StreamJoinMediaSettingsTests` pins that the bound values reach the
/// response DTO — and then the chain stops at the service boundary, which is where the interesting
/// half is.
///
/// **There are four copies of this setting list**: `MediaOptions` (the authority),
/// `MediaSettingsResponse` (what is sent), the `MediaSettings` TypeScript type (what the browser
/// can name), and `MEDIA_FALLBACK` (what it uses when nothing arrives). `mediaDefaults.ts` asks in
/// prose for the last of those to be kept in step — *"Values here are kept in sync with
/// MediaOptions.cs, which is the authority... Do not 'fix' a value here without reading that
/// file"*. A comment cannot fail a build. This can, and it is the same argument E-07 made for the
/// upload limit.
///
/// Each link fails silently and differently, which is why all four are checked:
///
///   * A field configured but not SENT leaves livekit-client on its own default — a thumbnail
///     pulling full resolution, or a single failed reconnect ejecting a student mid-lecture.
///   * A field sent under a name the browser does not know arrives as `undefined` and falls back,
///     so the server appears to be ignored. This is the `RAG_BASE_URL` shape, and it is the one
///     with no symptom at all.
///   * A field the browser names but never APPLIES is configuration that does nothing, which is
///     worse than absent: the next person to change it will believe they have.
///   * A fallback that disagrees with the server default makes the room behave differently
///     depending on whether the payload arrived — and nothing distinguishes the two cases.
///
/// Everything agrees today. The value of the rule is that it goes on agreeing.
/// </summary>
public sealed class MediaSettingsBrowserContractTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FrontendFile(string relative)
        => Path.Combine(RepoRoot, "front-end-web", "src", "features", "streaming", relative);

    /// <summary>The authority: every public property of MediaOptions, with its shipped default.</summary>
    private static Dictionary<string, object?> ServerDefaults()
    {
        var options = new MediaOptions();
        return typeof(MediaOptions)
            .GetProperties()
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .ToDictionary(property => Camel(property.Name), property => property.GetValue(options));
    }

    /// <summary>Field names on the response record — what is actually put on the wire.</summary>
    private static List<string> SentFields()
        => typeof(MediaSettingsResponse)
            .GetProperties()
            .Where(property => property.Name != "EqualityContract")
            .Select(property => Camel(property.Name))
            .ToList();

    /// <summary>Field names the browser's `MediaSettings` type declares.</summary>
    private static List<string> BrowserFields()
    {
        var source = File.ReadAllText(FrontendFile(Path.Combine("types", "index.ts")));
        var block = Regex.Match(source, @"export type MediaSettings = \{(.*?)\n\};", RegexOptions.Singleline);
        Assert.True(block.Success, "could not find the MediaSettings type — has it been renamed?");

        return Regex.Matches(block.Groups[1].Value, @"^\s*([A-Za-z0-9]+)\??:", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToList();
    }

    /// <summary>The browser's fallback object, as name -> literal.</summary>
    private static Dictionary<string, string> BrowserFallback()
    {
        var source = File.ReadAllText(FrontendFile(Path.Combine("config", "mediaDefaults.ts")));
        var block = Regex.Match(
            source, @"export const MEDIA_FALLBACK[^=]*= \{(.*?)\n\};", RegexOptions.Singleline);
        Assert.True(block.Success, "could not find MEDIA_FALLBACK — has it been renamed?");

        return Regex.Matches(block.Groups[1].Value, @"^\s*([A-Za-z0-9]+):\s*([^,\n]+),", RegexOptions.Multiline)
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value.Trim());
    }

    // --- the chain, link by link ----------------------------------------------------------

    [Fact]
    public void Every_setting_the_server_owns_is_actually_sent()
    {
        var missing = ServerDefaults().Keys
            .Where(name => name != "sectionName")
            .Except(SentFields())
            .ToList();

        Assert.True(
            missing.Count == 0,
            "These are configurable on the server and never reach the browser, so setting them "
            + "does nothing and livekit-client keeps its own default: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_field_that_is_sent_is_one_the_browser_can_name()
    {
        // The RAG_BASE_URL shape, at a different boundary. A field the TypeScript type does not
        // declare arrives as `undefined`, the validator substitutes a fallback, and the room comes
        // up looking fine — with the server's value silently discarded.
        var unknown = SentFields().Except(BrowserFields()).ToList();

        Assert.True(
            unknown.Count == 0,
            "The join payload carries these and the browser's MediaSettings type does not declare "
            + "them, so they are dropped in silence: " + string.Join(", ", unknown));
    }

    [Fact]
    public void Every_field_the_browser_names_is_read_from_the_SERVERS_object()
    {
        // Naming a field is not using it, and `toRoomOptions` is the only place these become
        // livekit-client options. It reads each one as `positiveIntOr(m.field, f.field)` — `m` is
        // what the server sent, `f` is the bundled fallback.
        //
        // So the assertion is on `m.field` specifically, not on the bare name. Looking for the
        // name alone is satisfied by the `f.field` half on its own, which is precisely the
        // interesting defect: a line changed to use only the fallback ignores the server's value
        // for that setting forever, and every other check here still passes. A mutation renaming
        // one occurrence survived the looser version of this rule.
        var applied = File.ReadAllText(FrontendFile(Path.Combine("config", "toRoomOptions.ts")));
        var inert = BrowserFields()
            .Where(field => !Regex.IsMatch(applied, $@"\bm\.{Regex.Escape(field)}\b"))
            .ToList();

        Assert.True(
            inert.Count == 0,
            "declared, and the server's value for them is never read: " + string.Join(", ", inert));
    }

    [Fact]
    public void The_browsers_fallback_agrees_with_the_server_default_value_for_value()
    {
        // The drift `mediaDefaults.ts` asks for in prose. When the payload does not arrive the
        // browser uses its own copy, so a disagreement means the room behaves differently
        // depending on something nobody can observe from inside it.
        var server = ServerDefaults();
        var browser = BrowserFallback();
        var disagreements = new List<string>();

        foreach (var (name, literal) in browser)
        {
            if (!server.TryGetValue(name, out var expected))
            {
                disagreements.Add($"{name} is in MEDIA_FALLBACK but not in MediaOptions");
                continue;
            }

            var actual = Normalize(literal);
            var wanted = Normalize(Render(expected));
            if (actual != wanted)
            {
                disagreements.Add($"{name}: MediaOptions says {wanted}, MEDIA_FALLBACK says {actual}");
            }
        }

        Assert.True(disagreements.Count == 0, string.Join("; ", disagreements));
    }

    [Fact]
    public void The_fallback_covers_every_setting_and_not_only_some_of_them()
    {
        // The other direction. A fallback missing a field is the worst of the four failures: it
        // only shows up when the server payload is absent, which is exactly when nobody is
        // watching, and `undefined` reaching livekit-client is not a default — it is whatever the
        // SDK does with undefined.
        var missing = SentFields().Except(BrowserFallback().Keys).ToList();

        Assert.True(missing.Count == 0, "no browser fallback for: " + string.Join(", ", missing));
    }

    // --- guards on the reading itself -------------------------------------------------------

    [Fact]
    public void All_four_copies_were_actually_read()
    {
        // Every rule above passes over an empty set, and three of the four sides are parsed out of
        // files this project does not compile. A renamed type or a reformatted object literal
        // would take the whole suite green.
        Assert.True(ServerDefaults().Count >= 18, "MediaOptions yielded almost no properties");
        Assert.True(SentFields().Count >= 18, "MediaSettingsResponse yielded almost no fields");
        Assert.True(BrowserFields().Count >= 18, $"the TS type yielded {BrowserFields().Count} fields");
        Assert.True(BrowserFallback().Count >= 18, $"MEDIA_FALLBACK yielded {BrowserFallback().Count} entries");
    }

    [Theory]
    [InlineData("adaptiveStream")]
    [InlineData("screenShareFramerate")]
    [InlineData("maxRetries")]
    [InlineData("stopMicTrackOnMute")]
    public void The_settings_this_feature_exists_for_are_present_on_every_side(string field)
    {
        // Named individually because these four are the reason the section exists at all — the
        // library shipped adaptiveStream off, screen share at 15fps, and maxRetries at 1. A rule
        // over a list cannot notice the list itself shrinking.
        Assert.Contains(field, ServerDefaults().Keys);
        Assert.Contains(field, SentFields());
        Assert.Contains(field, BrowserFields());
        Assert.Contains(field, BrowserFallback().Keys);
    }

    // --- helpers ------------------------------------------------------------------------------

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private static string Render(object? value) => value switch
    {
        bool flag => flag ? "true" : "false",
        null => "null",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>Strips the spellings that differ between the two languages, not the values.</summary>
    private static string Normalize(string literal)
        => literal.Trim().Trim('"', '\'').Replace("_", string.Empty);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "front-end-web")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
