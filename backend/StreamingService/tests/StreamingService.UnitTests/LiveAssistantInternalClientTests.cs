using System.Net;
using StreamingService.Infrastructure.Configuration;
using StreamingService.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace StreamingService.UnitTests;

public sealed class LiveAssistantInternalClientTests
{
    private const string BaseUrl = "http://live-assistant-service:8080/";
    private const string Secret = "dev-live-assistant-secret";

    private static LiveAssistantInternalClient CreateClient(CapturingHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        var options = Options.Create(new LiveAssistantOptions
        {
            BaseUrl = BaseUrl,
            InternalApiSecret = Secret,
            TimeoutSeconds = 5,
        });
        return new LiveAssistantInternalClient(httpClient, options, NullLogger<LiveAssistantInternalClient>.Instance);
    }

    [Fact]
    public async Task NotifySessionStarted_posts_start_with_body_and_secret_header()
    {
        var handler = new CapturingHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.Accepted));
        var client = CreateClient(handler);

        var sessionId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();
        var teacherIdentity = Guid.NewGuid().ToString();
        var roomName = sessionId.ToString();
        await client.NotifySessionStartedAsync(sessionId, classroomId, roomName, teacherIdentity);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://live-assistant-service:8080/api/internal/sessions/start", request.Uri!.AbsoluteUri);
        Assert.Equal(Secret, request.SecretHeader);

        // Body must use the assistant's camelCase keys and the exact values.
        var body = request.Body!;
        Assert.Contains($"\"sessionId\":\"{sessionId}\"", body);
        Assert.Contains($"\"classroomId\":\"{classroomId}\"", body);
        Assert.Contains($"\"roomName\":\"{roomName}\"", body);
        Assert.Contains($"\"teacherIdentity\":\"{teacherIdentity}\"", body);
    }

    [Fact]
    public async Task NotifySessionEnded_posts_stop_to_session_url_with_secret_header()
    {
        var handler = new CapturingHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = CreateClient(handler);

        var sessionId = Guid.NewGuid();
        await client.NotifySessionEndedAsync(sessionId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"http://live-assistant-service:8080/api/internal/sessions/{sessionId}/stop", request.Uri!.AbsoluteUri);
        Assert.Equal(Secret, request.SecretHeader);
    }

    [Fact]
    public async Task Throws_on_non_success_so_the_caller_can_treat_it_as_non_fatal()
    {
        var handler = new CapturingHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.NotifySessionStartedAsync(Guid.NewGuid(), Guid.NewGuid(), "room", "teacher"));

        // Single attempt — session start must stay snappy; no retries for the assistant.
        Assert.Single(handler.Requests);
    }
}
