using System.Text.RegularExpressions;
using ClassroomService.Infrastructure.Configuration;

namespace ClassroomService.UnitTests;

/// <summary>
/// One upload limit, three copies, and only one of them can read the others (test-plan E-07).
///
/// The size a teacher may upload is enforced in three places: the resource filter before the body
/// is read, the file service on the exact file length, and nginx's `client_max_body_size` at the
/// gateway. The first two come from `Uploads:MaxFileSizeBytes`. **nginx cannot read that setting**
/// — `IUploadSettings`' own comment says so — and nginx is the one that acts first.
///
/// So the failure is entirely one-directional and entirely silent. If nginx's number ever falls
/// below the application's, enforcement moves back to the proxy: the upload is refused before it
/// reaches any service, with a bare HTML 413 the frontend cannot parse, showing a teacher a broken
/// page instead of "this file is too large". Nothing about that failure points at nginx. The
/// service logs nothing, because it was never asked.
///
/// `nginx.conf` already carries the warning — "Raising the app limit past this line silently moves
/// enforcement back to nginx — change both." A comment cannot fail a build. This can.
///
/// The same shape, in the same file, for the allow-list: `UploadOptions` says it mirrors
/// RagService's extractor types and asks that the two be kept in step. Since §7.5b that drift is
/// no longer merely wasteful — the extractor router now refuses bytes it does not recognise, so a
/// type accepted here and unknown there uploads cleanly and then fails indexing, which a teacher
/// sees as a file that never becomes searchable.
///
/// Lives in this project because it needs no service of its own; it reads files.
/// </summary>
public sealed class UploadLimitConsistencyTests
{
    private static readonly string BackendRoot = FindBackendRoot();

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

    // --- nginx against the application ---------------------------------------------------

    [Fact]
    public void Nginx_accepts_at_least_what_the_application_is_willing_to_examine()
    {
        // The application must be the one that refuses, because it is the only one that can
        // refuse in a shape the frontend understands. That means nginx has to pass through
        // everything up to the app's own ceiling — the file limit PLUS the multipart overhead
        // the filter already allows for, since nginx measures the whole request body and the
        // application's limit is on the file inside it.
        var options = new UploadOptions();
        var applicationCeiling = options.MaxFileSizeBytes + options.MultipartOverheadBytes;

        Assert.True(
            NginxBodyLimitBytes() >= applicationCeiling,
            $"nginx accepts {NginxBodyLimitBytes()} bytes but the application will examine up to "
            + $"{applicationCeiling}. Everything in between is refused at the proxy with an HTML "
            + "413 the frontend cannot parse, and nothing in any service log will say so. "
            + "Raise client_max_body_size in backend/nginx.conf, or lower Uploads:MaxFileSizeBytes.");
    }

    [Fact]
    public void The_application_limit_is_the_one_a_teacher_actually_meets()
    {
        // The other direction, and the reason this is not simply "nginx should be huge". nginx
        // buffers the request body before proxying it, so its limit is also a memory bound. The
        // gap wants to be enough for framing and no more — big enough that the app always wins,
        // small enough that the proxy is still a backstop.
        var options = new UploadOptions();
        var applicationCeiling = options.MaxFileSizeBytes + options.MultipartOverheadBytes;
        var headroom = NginxBodyLimitBytes() - applicationCeiling;

        Assert.InRange(headroom, 0, options.MaxFileSizeBytes);
    }

    [Fact]
    public void The_directive_is_actually_found_in_the_file()
    {
        // Without this, a rename or a reformat that makes the regex miss would leave the rules
        // above passing against a limit nobody read — and the parse would be reported as the
        // value zero, which no assertion above would like. Named separately so a failure says
        // "the file changed shape", not "the limit is wrong".
        var config = File.ReadAllText(Path.Combine(BackendRoot, "nginx.conf"));

        Assert.Matches(@"client_max_body_size\s+\d+[kKmMgG]?;", config);
    }

    [Theory]
    [InlineData("client_max_body_size 64m;", 64L * 1024 * 1024)]
    [InlineData("client_max_body_size 1024k;", 1024L * 1024)]
    [InlineData("client_max_body_size 2G;", 2L * 1024 * 1024 * 1024)]
    [InlineData("client_max_body_size 1048576;", 1048576L)]
    public void The_size_suffixes_are_read_the_way_nginx_reads_them(string directive, long expected)
    {
        // nginx's suffixes are binary (k = 1024), and are case-insensitive. Reading `m` as a
        // million would understate the limit by 5% and make this rule fail on a correct config —
        // the kind of false alarm that gets a test deleted rather than fixed.
        Assert.Equal(expected, ParseNginxSize(directive));
    }

    // --- the allow-list against the extractor that has to read the file -------------------

    [Fact]
    public void Every_accepted_content_type_is_one_RagService_can_extract()
    {
        var extractable = RagServiceConstants("_CONTENT_TYPES");
        var accepted = new UploadOptions().AllowedContentTypes;

        var orphans = accepted.Where(type => !extractable.Contains(type)).ToList();

        Assert.True(
            orphans.Count == 0,
            "These content types are accepted at upload but no RagService extractor handles them, "
            + "so the file uploads cleanly and then fails indexing — which a teacher sees as a file "
            + $"that never becomes searchable: {string.Join(", ", orphans)}");
    }

    [Fact]
    public void Every_accepted_extension_is_one_RagService_can_extract()
    {
        var extractable = RagServiceConstants("_EXTENSIONS");
        var accepted = new UploadOptions().AllowedExtensions;

        var orphans = accepted.Where(extension => !extractable.Contains(extension)).ToList();

        Assert.True(orphans.Count == 0, $"Accepted but not extractable: {string.Join(", ", orphans)}");
    }

    [Fact]
    public void The_extractor_is_read_from_source_and_not_from_an_empty_set()
    {
        // The vacuum guard. If RagService moves this file, or the frozenset syntax changes, both
        // rules above would find nothing missing from nothing and pass forever.
        Assert.True(RagServiceConstants("_CONTENT_TYPES").Count >= 6);
        Assert.True(RagServiceConstants("_EXTENSIONS").Count >= 6);
    }

    // --- helpers ---------------------------------------------------------------------------

    private static long NginxBodyLimitBytes()
        => ParseNginxSize(File.ReadAllText(Path.Combine(BackendRoot, "nginx.conf")));

    /// <summary>Reads the first `client_max_body_size` directive, honouring nginx's k/m/g suffixes.</summary>
    private static long ParseNginxSize(string config)
    {
        var match = Regex.Match(config, @"client_max_body_size\s+(\d+)([kKmMgG]?)\s*;");
        Assert.True(match.Success, "no client_max_body_size directive found in nginx.conf");

        var value = long.Parse(match.Groups[1].Value);
        return char.ToLowerInvariant(match.Groups[2].Value.FirstOrDefault()) switch
        {
            'k' => value * 1024,
            'm' => value * 1024 * 1024,
            'g' => value * 1024 * 1024 * 1024,
            _ => value,
        };
    }

    /// <summary>
    /// The values RagService's extractors declare, read from `_support.py`.
    ///
    /// Read rather than copied, for the reason the whole file exists: a table kept here would
    /// agree with RagService on the day it was written and never again.
    /// </summary>
    private static HashSet<string> RagServiceConstants(string suffix)
    {
        var source = File.ReadAllText(Path.Combine(
            BackendRoot, "RagService", "app", "infrastructure", "extraction", "_support.py"));

        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match declaration in Regex.Matches(
            source, @"\w+" + Regex.Escape(suffix) + @"\s*=\s*frozenset\(\s*\{(.*?)\}", RegexOptions.Singleline))
        {
            foreach (Match value in Regex.Matches(declaration.Groups[1].Value, "\"([^\"]+)\""))
            {
                values.Add(value.Groups[1].Value);
            }
        }

        return values;
    }
}
