using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UserManagementService.Api.Middleware;
using UserManagementService.Application.Common;

using System.Text.Json;

namespace UserManagementService.UnitTests.UserAccounts;

/// <summary>
/// What a caller is told when something throws (test-plan S-14, the client's half).
///
/// This came out of S-14 — "bulk approve retried after a timeout". The server's half of that is
/// idempotency, and it holds (see <c>UserStatusRetryTests</c>). The client's half is what it
/// actually receives when it gives up, and that runs through here: one handler, registered
/// globally, deciding the status code and the body for every endpoint in the service. It had no
/// tests at all, and the two things it got wrong are both specific to the abandoned-request case
/// this row is about.
/// </summary>
public sealed class GlobalExceptionHandlerTests
{
    [Fact]
    public async Task A_client_that_gave_up_is_not_recorded_as_a_server_error()
    {
        // The defect. When a client times out and disconnects, ASP.NET cancels the request token
        // and EF throws OperationCanceledException — which fell through to the catch-all and was
        // logged at Error as "an unhandled exception occurred", then answered 500.
        //
        // Nobody is listening: the socket is already gone, so the 500 is recorded for a client
        // that will never see it. What remains is a log full of manufactured server errors, one
        // per abandoned request — and the retry storm that follows a timeout produces them in
        // bursts. That is the noise that hides the real failure underneath it, and the timeout
        // scenario is exactly when somebody is reading this log.
        var (handler, logger) = Handler();
        var context = Context();

        await handler.TryHandleAsync(context, new OperationCanceledException(), CancellationToken.None);

        Assert.Equal(499, context.Response.StatusCode);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task A_cancelled_task_counts_as_the_same_thing()
    {
        // TaskCanceledException is what actually arrives most of the time. It derives from
        // OperationCanceledException, so one arm covers both — but only if the arm is written
        // against the base type, and a switch arm naming the derived one would look correct.
        var (handler, _) = Handler();
        var context = Context();

        await handler.TryHandleAsync(context, new TaskCanceledException(), CancellationToken.None);

        Assert.Equal(499, context.Response.StatusCode);
    }

    [Fact]
    public async Task An_unexpected_failure_does_not_hand_its_message_to_the_caller()
    {
        // The other defect: Detail was the raw exception message for every exception, including
        // the ones nobody planned for. A Postgres failure arrives carrying the SQL, the table and
        // the constraint; a misconfiguration arrives carrying the connection string it tried.
        // Those went to whoever made the request.
        //
        // The mapped exceptions above are ours and their messages are written to be read by a
        // user. The catch-all is, by definition, the case where nobody decided that.
        var (handler, logger) = Handler();
        var context = Context();
        var leaky = new InvalidCastException(
            "42P01: relation \"Users\" does not exist; Host=postgres;Password=hunter2");

        await handler.TryHandleAsync(context, leaky, CancellationToken.None);

        var problem = await ReadBody(context);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.DoesNotContain("hunter2", problem);
        Assert.DoesNotContain("42P01", problem);
        // ...and it is still in the log, where it belongs.
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("hunter2"));
    }

    [Theory]
    [InlineData(StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status409Conflict)]
    [InlineData(StatusCodes.Status401Unauthorized)]
    public async Task A_mapped_failure_still_explains_itself(int expectedStatus)
    {
        // The counterweight to the case above. These messages are the product's own words —
        // "Account pending approval", "Cannot accept an account that is currently 'Rejected'" —
        // and blanking them along with the leak would be a worse bug than the leak.
        var exception = ExceptionFor(expectedStatus);
        var (handler, _) = Handler();
        var context = Context();

        await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.Equal(expectedStatus, context.Response.StatusCode);
        Assert.Contains(exception.Message, await ReadBody(context));
    }

    [Fact]
    public async Task A_concurrency_conflict_is_a_conflict_and_not_a_crash()
    {
        // The one a racing retry produces. Two identical bulk requests in flight — the second
        // arriving because the first appeared to time out — collide on User.Version, and the
        // loser gets DbUpdateConcurrencyException. 409 tells the caller the work is contended
        // rather than broken, which is the difference between retrying and paging somebody.
        var (handler, _) = Handler();
        var context = Context();

        await handler.TryHandleAsync(context, new DbUpdateConcurrencyException(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
    }

    [Fact]
    public async Task Nothing_is_written_over_a_response_that_already_started()
    {
        // An exception thrown after the first bytes are on the wire cannot be turned into a
        // ProblemDetails — the status line is already sent. Writing anyway appends JSON to a
        // half-finished body, so the client gets a truncated payload with an error object glued
        // to the end of it, under whatever status code was already promised.
        var (handler, logger) = Handler();
        var context = Context();
        var body = new StartedResponseBody();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature(body));

        var handled = await handler.TryHandleAsync(context, new Exception("too late"), CancellationToken.None);

        Assert.False(handled, "the middleware must be allowed to abort the connection instead");
        Assert.Equal(0, body.BytesWritten);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Every_failure_reaches_the_log_exactly_once()
    {
        // The handler is the last place an exception is seen. A path that returns without
        // logging loses it entirely, and one that logs twice makes the count in an alert wrong.
        var (handler, logger) = Handler();

        foreach (var exception in new Exception[]
        {
            new NotFoundException("gone"),
            new ArgumentException("bad"),
            new OperationCanceledException(),
            new Exception("boom"),
        })
        {
            await handler.TryHandleAsync(Context(), exception, CancellationToken.None);
        }

        Assert.Equal(4, logger.Entries.Count);
    }

    // --- helpers ------------------------------------------------------------------------

    private static Exception ExceptionFor(int status) => status switch
    {
        StatusCodes.Status404NotFound => new NotFoundException("User not found."),
        StatusCodes.Status400BadRequest => new ArgumentException("Select at least one account."),
        StatusCodes.Status409Conflict => new InvalidOperationException("You cannot change the status of your own account."),
        StatusCodes.Status401Unauthorized => new UnauthorizedAccessException("Invalid credentials."),
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static (GlobalExceptionHandler Handler, RecordingHandlerLogger Logger) Handler()
    {
        var logger = new RecordingHandlerLogger();
        return (new GlobalExceptionHandler(logger), logger);
    }

    /// <summary>
    /// A context with a REAL body stream. <c>DefaultHttpContext</c> writes to <c>Stream.Null</c>,
    /// so without this every "the body does not contain the connection string" assertion would
    /// pass against a body that was empty for reasons having nothing to do with the fix.
    /// </summary>
    private static DefaultHttpContext Context()
        => new() { Response = { Body = new MemoryStream() } };

    private static async Task<string> ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var raw = await reader.ReadToEndAsync();
        // Parsed rather than string-matched, so an assertion cannot pass on a field name.
        return raw.Length == 0 ? raw : JsonSerializer.Deserialize<JsonElement>(raw).ToString();
    }

    /// <summary>
    /// A response feature that reports the response as already begun.
    ///
    /// It holds its own status, headers and body rather than delegating to the HttpResponse.
    /// Delegating is the obvious way to write this and it is a trap: `HttpResponse.StatusCode`
    /// reads from whichever `IHttpResponseFeature` is installed, so a feature that forwards back
    /// to it recurses until the stack runs out — and a StackOverflowException kills the test host
    /// outright rather than failing a test, so the whole run reports "passed" with most of the
    /// suite silently never executed.
    /// </summary>
    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public StartedResponseFeature(Stream body) => Body = body;

        public bool HasStarted => true;
        public Stream Body { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }

        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }

    private sealed class StartedResponseBody : Stream
    {
        public int BytesWritten { get; private set; }

        public override void Write(byte[] buffer, int offset, int count) => BytesWritten += count;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

internal sealed class RecordingHandlerLogger : ILogger<GlobalExceptionHandler>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;

    public void Log<TState>(
        LogLevel level, EventId id, TState state, Exception? error,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((level, formatter(state, error) + " " + (error?.Message ?? string.Empty)));
}
