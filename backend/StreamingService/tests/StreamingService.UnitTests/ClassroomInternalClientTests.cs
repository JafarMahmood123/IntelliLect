using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamingService.Infrastructure.Configuration;
using StreamingService.Infrastructure.Services;

namespace StreamingService.UnitTests;

/// <summary>
/// The membership question this service cannot answer itself, and the one rule that matters about
/// how it asks: **it fails closed** (test-plan G-02, Q-11's shape).
///
/// Every other internal client in this repository is best-effort — the assistant is an enhancement,
/// a failed notification costs a feature. This one answers an authorization question, and there is
/// no such thing as a best-effort authorization decision. §7b already found the opposite shape on
/// the internal secret itself: a guard that admitted everybody precisely when it was misconfigured.
///
/// So each of these cases exists because it is a distinct way for the question to go unanswered,
/// and a client that handled four of them and threw on the fifth would take the fifth all the way
/// up to a 500 — which, on a route whose job is to decide who may enter a lecture, is a far better
/// outcome than admitting them but is still not the one that was designed.
/// </summary>
public sealed class ClassroomInternalClientTests
{
    private const string BaseUrl = "http://classroom-service:8080/";
    private const string Secret = "dev-internal-secret";

    private static readonly Guid ClassroomId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private static (ClassroomInternalClient Client, RecordingLogger<ClassroomInternalClient> Log)
        Create(HttpMessageHandler handler)
    {
        var log = new RecordingLogger<ClassroomInternalClient>();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        var options = Options.Create(new ClassroomServiceOptions
        {
            BaseUrl = BaseUrl,
            InternalApiSecret = Secret,
            TimeoutSeconds = 3,
        });
        return (new ClassroomInternalClient(httpClient, options, log), log);
    }

    private static CapturingHttpMessageHandler Responds(HttpStatusCode status, string? json = null)
        => new(() => new HttpResponseMessage(status)
        {
            Content = json is null ? null : new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

    // --- the happy path -------------------------------------------------------------------------

    [Fact]
    public async Task It_asks_the_classroom_about_the_user_and_presents_the_shared_secret()
    {
        var handler = Responds(HttpStatusCode.OK, """{"isMember":true,"isTeacher":false}""");
        var (client, _) = Create(handler);

        await client.GetAccessAsync(ClassroomId, UserId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            $"http://classroom-service:8080/api/internal/classrooms/{ClassroomId}/access/{UserId}",
            request.Uri!.AbsoluteUri);
        // No user token travels here — the shared secret IS the authorization on that route, so a
        // request that forgot it would be refused and read, from here, as "not a member".
        Assert.Equal(Secret, request.SecretHeader);
    }

    [Fact]
    public async Task Both_flags_come_back_as_the_classroom_reported_them()
    {
        // Two flags rather than one, and they are not interchangeable: the teacher is entitled to
        // the room without being an enrolled student, and the caller needs to tell them apart to
        // decide publishing rights. Distinct values so a client that returned one for both, or
        // crossed them, cannot pass.
        var (teacher, _) = Create(Responds(HttpStatusCode.OK, """{"isMember":true,"isTeacher":true}"""));
        var (student, _) = Create(Responds(HttpStatusCode.OK, """{"isMember":true,"isTeacher":false}"""));

        var asTeacher = await teacher.GetAccessAsync(ClassroomId, UserId);
        var asStudent = await student.GetAccessAsync(ClassroomId, UserId);

        Assert.True(asTeacher is { IsMember: true, IsTeacher: true });
        Assert.True(asStudent is { IsMember: true, IsTeacher: false });
    }

    [Fact]
    public async Task A_stranger_is_reported_as_neither()
    {
        var (client, _) = Create(Responds(HttpStatusCode.OK, """{"isMember":false,"isTeacher":false}"""));

        Assert.Equal(ClassroomAccessNone, await client.GetAccessAsync(ClassroomId, UserId));
    }

    // --- failing closed -------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]        // the classroom is gone, or the databases disagree
    [InlineData(HttpStatusCode.Unauthorized)]    // the shared secret is wrong or unset
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]      // nginx or the container is mid-restart
    public async Task An_unsuccessful_answer_is_refused_rather_than_assumed(HttpStatusCode status)
    {
        var (client, log) = Create(Responds(status));

        Assert.Equal(ClassroomAccessNone, await client.GetAccessAsync(ClassroomId, UserId));

        // And it must be visible. The caller deliberately cannot distinguish "not a member" from
        // "could not ask" — that collapse is the safe part of the design — so this log is the only
        // place the difference survives, and an operator staring at "nobody can join" needs it.
        Assert.Contains(log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task An_unreachable_classroom_service_is_refused_rather_than_thrown()
    {
        // Connection refused, DNS failure, the container not up yet. A throw here would surface as
        // a 500 on the join route, which is safe but is a different contract from the one the
        // caller was written against — and one that a well-meaning catch upstream could turn into
        // a default of "let them in".
        var (client, log) = Create(new ThrowingHandler(new HttpRequestException("connection refused")));

        Assert.Equal(ClassroomAccessNone, await client.GetAccessAsync(ClassroomId, UserId));
        Assert.Contains(log.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task A_timeout_is_refused_rather_than_thrown()
    {
        // HttpClient reports its own timeout as TaskCanceledException with no cancellation
        // requested — the case a naive `catch (OperationCanceledException)` re-throws, because it
        // looks exactly like the caller hanging up.
        var (client, _) = Create(new ThrowingHandler(new TaskCanceledException("timed out")));

        Assert.Equal(ClassroomAccessNone, await client.GetAccessAsync(ClassroomId, UserId));
    }

    [Fact]
    public async Task A_body_that_cannot_be_read_is_refused()
    {
        // 200 with something that is not the expected object: a proxy's error page, an HTML login
        // redirect, a schema that moved. The status line said yes and the content says nothing.
        var (client, log) = Create(Responds(HttpStatusCode.OK, "\"not-an-object\""));

        Assert.Equal(ClassroomAccessNone, await client.GetAccessAsync(ClassroomId, UserId));
        Assert.Contains(log.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task The_callers_own_cancellation_still_propagates()
    {
        // The one exception to catching everything. A client that hung up is not a failed
        // membership check and must not be logged as one — §11.7's 499 lesson, where manufactured
        // server errors arrived in bursts at exactly the moment somebody was reading the log.
        var (client, log) = Create(new ThrowingHandler(new TaskCanceledException("caller left")));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAccessAsync(ClassroomId, UserId, cancelled.Token));
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Error);
    }

    private static readonly Application.Abstractions.ClassroomAccess ClassroomAccessNone =
        Application.Abstractions.ClassroomAccess.None;

    /// <summary>Fails the way a network does, rather than returning a response.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _failure;
        public ThrowingHandler(Exception failure) => _failure = failure;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw _failure;
        }
    }
}
