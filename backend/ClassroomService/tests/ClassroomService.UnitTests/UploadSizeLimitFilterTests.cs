using System.Text;
using ClassroomService.Infrastructure.Configuration;
using ClassroomService.Presentation.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace ClassroomService.UnitTests;

/// <summary>
/// An oversized upload is refused before its body is read (test-plan E-02, E-03).
///
/// E-03 was marked Integration and `new` — never written. `UploadSizeLimitFilter` is the code that
/// answers it, and **no test had ever executed a line of it**, which is how the fourth limit below
/// went unnoticed.
///
/// It needs no host. A resource filter takes an `HttpContext` and a continuation; `DefaultHttpContext`
/// is an `HttpContext`, and "before the body is read" is observable as "the continuation never ran".
/// Where a real reader is needed — for the multipart case — the framework's own `FormFeature` reads
/// a real multipart body from a real stream, in process.
///
/// **The defect.** `IUploadSettings` says the one configured value is "enforced in three places —
/// the resource filter, the file service, and nginx". There are four limits in this request path.
/// The fourth is <see cref="FormOptions.MultipartBodyLengthLimit"/>, a framework default of 128 MB
/// applied by the multipart reader during model binding, derived from nothing and mentioned
/// nowhere. Raise `Uploads:MaxFileSizeBytes` above it — E-07's rule will make you raise nginx to
/// match, and this filter raises Kestrel — and a file between 128 MB and the new limit passes both
/// guards, is buffered in full, and then dies inside model binding with an `InvalidDataException`.
/// `GlobalExceptionHandler` turns that into a **500 "An unexpected error occurred"**, which is
/// precisely the untyped failure the other two guards exist to avoid. Safe today only because
/// 50 MB happens to be less than 128 MB — the same "safe by accident of the current value" shape
/// B-10 found in the nginx route table.
/// </summary>
public sealed class UploadSizeLimitFilterTests
{
    private const long MaxFileSize = 4096;
    private const long Overhead = 64;
    private const long Ceiling = MaxFileSize + Overhead;

    /// <summary>Records whether the pipeline past the filter was reached.</summary>
    private sealed class Continuation
    {
        public bool Ran { get; private set; }

        public ResourceExecutionDelegate DelegateFor(ResourceExecutingContext context)
            => () =>
            {
                Ran = true;
                return Task.FromResult(
                    new ResourceExecutedContext(context, context.Filters) { Result = new OkResult() });
            };
    }

    private static ResourceExecutingContext ContextFor(HttpContext http)
        => new(
            new ActionContext(http, new RouteData(), new ActionDescriptor()),
            [],
            []);

    private static async Task<(ResourceExecutingContext Context, bool ContinuationRan)> RunAsync(
        HttpContext http, long maxFileSize = MaxFileSize, long overhead = Overhead)
    {
        var filter = new UploadSizeLimitFilter(new FakeUploadSettings
        {
            MaxFileSizeBytes = maxFileSize,
            MultipartOverheadBytes = overhead,
        });

        var context = ContextFor(http);
        var next = new Continuation();
        await filter.OnResourceExecutionAsync(context, next.DelegateFor(context));
        return (context, next.Ran);
    }

    private static DefaultHttpContext Request(long? contentLength, string path = "/api/classrooms/x/files")
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = path;
        http.Request.ContentLength = contentLength;
        return http;
    }

    // --- E-03: refused on Content-Length, before the body is read ------------------------------

    [Fact]
    public async Task An_oversized_declared_length_never_reaches_the_action()
    {
        var (context, ran) = await RunAsync(Request(Ceiling + 1));

        // This assertion IS E-03. "Before the body is buffered" is not a property of the response
        // code — a 413 produced after reading 2 GB is still a 413. It is the property that nothing
        // downstream of the filter ever ran, and therefore nothing ever asked for the body.
        Assert.False(ran, "the request continued past the size guard, so the body was read anyway");
        Assert.NotNull(context.Result);
    }

    [Fact]
    public async Task The_refusal_is_a_typed_problem_document_and_not_a_bare_413()
    {
        // E-02's second half. nginx answers an over-limit request with an HTML error page the
        // frontend cannot parse; the point of refusing inside the service is that a teacher sees
        // "this file is too large" instead of a broken page.
        var (context, _) = await RunAsync(Request(Ceiling + 1));

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, result.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, problem.Status);
        // Names the configured FILE limit, not the envelope ceiling — the number a teacher can act
        // on is the one the upload control shows them.
        Assert.Contains(MaxFileSize.ToString(), problem.Detail);
        Assert.Equal("/api/classrooms/x/files", problem.Instance);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(MaxFileSize)]
    [InlineData(Ceiling)]
    public async Task A_request_within_the_ceiling_is_passed_through(long declaredLength)
    {
        // Both ends of the boundary, and the exact ceiling. E-01 covers the file-level equivalent;
        // this is the envelope, which is larger by the multipart overhead — and getting that
        // backwards would refuse a file of exactly the advertised maximum.
        var (context, ran) = await RunAsync(Request(declaredLength));

        Assert.True(ran);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task One_byte_over_the_ceiling_is_refused()
    {
        var (_, ran) = await RunAsync(Request(Ceiling + 1));
        Assert.False(ran);
    }

    [Fact]
    public async Task A_request_that_declares_no_length_is_passed_on_to_the_streaming_guards()
    {
        // Chunked transfer-encoding, or a client that simply omits it. The up-front check cannot
        // fire, and refusing outright would break a legitimate client — so it continues, and the
        // two byte-counting guards below are what stop it.
        var (context, ran) = await RunAsync(Request(contentLength: null));

        Assert.True(ran);
        Assert.Null(context.Result);
    }

    // --- the byte-counting guards --------------------------------------------------------------

    [Fact]
    public async Task Kestrels_per_request_ceiling_is_raised_to_the_configured_limit()
    {
        // Kestrel's default is 30 MB, which is BELOW the configured 50 MB — so without this the
        // deployment's own setting would be unreachable and the effective limit would be a
        // framework constant nobody chose.
        var http = Request(contentLength: null);
        var size = new FakeMaxRequestBodySizeFeature();
        http.Features.Set<IHttpMaxRequestBodySizeFeature>(size);

        await RunAsync(http);

        Assert.Equal(Ceiling, size.MaxRequestBodySize);
    }

    [Fact]
    public async Task A_ceiling_that_cannot_be_changed_is_not_fatal()
    {
        // Read-only once the body has started being read. The filter must carry on — the exact
        // per-file check still refuses the file — rather than throwing and turning an oversized
        // upload into a 500.
        var http = Request(contentLength: null);
        var size = new FakeMaxRequestBodySizeFeature { IsReadOnly = true, MaxRequestBodySize = 99 };
        http.Features.Set<IHttpMaxRequestBodySizeFeature>(size);

        var (context, ran) = await RunAsync(http);

        Assert.Equal(99, size.MaxRequestBodySize);
        Assert.True(ran);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task A_server_with_no_size_feature_at_all_is_not_fatal()
    {
        // DefaultHttpContext has no such feature, which is also true of the test host.
        var (_, ran) = await RunAsync(Request(contentLength: null));
        Assert.True(ran);
    }

    // --- the fourth limit, which nobody had counted --------------------------------------------

    [Fact]
    public async Task The_multipart_reader_enforces_the_configured_ceiling_and_not_a_framework_default()
    {
        // The defect, made observable with small numbers. In production the trigger is a
        // configured limit above 128 MB; the MECHANISM is the same one exercised here — the
        // multipart reader applies its own limit, derived from nothing, during model binding and
        // after both other guards have passed the request.
        //
        // This reads a real multipart body with the framework's real FormFeature, so what is
        // asserted is the limit actually in force rather than a value passed to a setter.
        var http = MultipartRequest(bodyBytes: (int)Ceiling * 2);

        await RunAsync(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => http.Request.ReadFormAsync());
    }

    [Fact]
    public async Task A_multipart_body_within_the_ceiling_still_reads_normally()
    {
        // The other direction, and the guard against "fixing" this by making the reader refuse
        // everything. A legitimate upload must still bind.
        var http = MultipartRequest(bodyBytes: 256);

        await RunAsync(http);
        var form = await http.Request.ReadFormAsync();

        Assert.Single(form.Files);
        Assert.Equal(256, form.Files[0].Length);
    }

    [Fact]
    public async Task Without_the_filter_the_same_body_is_read_in_full()
    {
        // What the fourth limit was doing before: nothing. The framework default is 128 MB, so a
        // body far above the configured ceiling is buffered without complaint. This is the state
        // the test above would find if the derivation were removed — asserted here so the pairing
        // is visible rather than implied.
        var http = MultipartRequest(bodyBytes: (int)Ceiling * 2);

        var form = await http.Request.ReadFormAsync();

        Assert.Equal(Ceiling * 2, form.Files[0].Length);
    }

    [Fact]
    public void The_configured_ceiling_is_currently_below_the_framework_default()
    {
        // Why this was never seen in production, stated rather than left to be rediscovered. It is
        // NOT what keeps the system safe — the filter now derives the limit either way — but if
        // this ever stops being true, the failure it used to hide becomes reachable in any
        // deployment that has not picked up the fix.
        var configured = new UploadOptions();
        var ceiling = configured.MaxFileSizeBytes + configured.MultipartOverheadBytes;

        Assert.True(
            ceiling < FormOptions.DefaultMultipartBodyLengthLimit,
            $"Uploads:MaxFileSizeBytes is now {configured.MaxFileSizeBytes}, whose envelope exceeds "
            + $"the multipart reader's {FormOptions.DefaultMultipartBodyLengthLimit}-byte default. "
            + "The filter derives its own limit so this still works — but any deployment without "
            + "that derivation answers 500 instead of 413 for files in between.");
    }

    [Fact]
    public void Nothing_configures_FormOptions_application_wide()
    {
        // The filter replaces the request's IFormFeature outright, which discards any
        // application-wide FormOptions. Nothing sets one today; if something starts to, this
        // filter would silently drop it and the two facts need reconciling deliberately.
        var sources = Directory
            .EnumerateFiles(Path.Combine(BackendRoot, "ClassroomService", "src"), "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(sources);
        Assert.DoesNotContain(
            sources,
            path => File.ReadAllText(path).Contains("Configure<FormOptions>", StringComparison.Ordinal));
    }

    // --- the wiring, which no test would otherwise notice ---------------------------------------

    [Fact]
    public void Every_action_that_accepts_a_file_carries_the_size_filter()
    {
        // A rule over the controllers rather than a note about the one that exists. Everything
        // above tests the filter; none of it would notice a SECOND upload endpoint shipping
        // without it, and that is the failure this codebase keeps finding — three EmailService
        // consumers with no retry, two more in ClassroomService and StreamingService.
        var controllers = Path.Combine(
            BackendRoot, "ClassroomService", "src", "ClassroomService.Presentation", "Controllers");

        var actions = Directory
            .EnumerateFiles(controllers, "*.cs", SearchOption.AllDirectories)
            .SelectMany(path => File.ReadAllText(path)
                .Split("\n    [Http")
                .Skip(1)
                .Select(block => (File: Path.GetFileName(path), Block: block)))
            .Where(action => action.Block.Contains("IFormFile", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(actions);

        var unguarded = actions
            .Where(action => !action.Block.Contains("ServiceFilter(typeof(UploadSizeLimitFilter))",
                StringComparison.Ordinal))
            .Select(action => action.File)
            .ToList();

        Assert.True(
            unguarded.Count == 0,
            "These actions accept a file with no size guard, so the whole body is buffered before "
            + "anything checks it: " + string.Join(", ", unguarded));
    }

    [Fact]
    public void The_filter_is_registered_in_the_container()
    {
        // [ServiceFilter] resolves from DI, so deleting one line in Program.cs turns every upload
        // into a 500 — and Program.cs is excluded from coverage by the shared runsettings, so
        // nothing else in this suite reads it.
        var program = File.ReadAllText(Path.Combine(
            BackendRoot, "ClassroomService", "src", "ClassroomService.Api", "Program.cs"));

        Assert.Contains("AddScoped<UploadSizeLimitFilter>()", program);
    }

    // --- helpers --------------------------------------------------------------------------------

    /// <summary>A real multipart/form-data request whose file part is <paramref name="bodyBytes"/> long.</summary>
    private static DefaultHttpContext MultipartRequest(int bodyBytes)
    {
        const string boundary = "----IntelliLectTestBoundary";
        var header = $"--{boundary}\r\n"
                     + "Content-Disposition: form-data; name=\"file\"; filename=\"lecture.pdf\"\r\n"
                     + "Content-Type: application/pdf\r\n\r\n";
        var footer = $"\r\n--{boundary}--\r\n";

        var body = new MemoryStream();
        body.Write(Encoding.ASCII.GetBytes(header));
        body.Write(new byte[bodyBytes]);
        body.Write(Encoding.ASCII.GetBytes(footer));
        body.Position = 0;

        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = "/api/classrooms/x/files";
        http.Request.ContentType = $"multipart/form-data; boundary={boundary}";
        http.Request.Body = body;
        // Deliberately absent: an honest Content-Length would be caught by the up-front check, and
        // the point of this pair is what happens to a request that gets PAST it.
        http.Request.ContentLength = null;
        return http;
    }

    private sealed class FakeMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; init; }
        public long? MaxRequestBodySize { get; set; }
    }

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
}
