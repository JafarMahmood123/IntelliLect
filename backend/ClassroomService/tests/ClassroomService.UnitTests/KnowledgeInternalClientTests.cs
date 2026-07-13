using System.Net;
using ClassroomService.Infrastructure.Configuration;
using ClassroomService.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClassroomService.UnitTests;

public sealed class KnowledgeInternalClientTests
{
    private const string BaseUrl = "http://knowledge-service:8080/";
    private const string Secret = "test-internal-secret";

    private static KnowledgeInternalClient CreateClient(CapturingHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        var options = Options.Create(new KnowledgeServiceOptions
        {
            BaseUrl = BaseUrl,
            InternalApiSecret = Secret,
            TimeoutSeconds = 10,
        });
        return new KnowledgeInternalClient(httpClient, options, NullLogger<KnowledgeInternalClient>.Instance);
    }

    [Fact]
    public async Task NotifyFileUploaded_posts_ingest_with_body_and_secret_header()
    {
        var handler = new CapturingHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.Accepted));
        var client = CreateClient(handler);

        var fileId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();
        await client.NotifyFileUploadedAsync(fileId, classroomId, "classrooms/x/key.pdf", "lecture.pdf", "application/pdf");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://knowledge-service:8080/api/internal/documents/ingest", request.Uri!.AbsoluteUri);
        Assert.Equal(Secret, request.SecretHeader);

        // Body must use KnowledgeService's camelCase aliases and the exact values.
        var body = request.Body!;
        Assert.Contains($"\"fileId\":\"{fileId}\"", body);
        Assert.Contains($"\"classroomId\":\"{classroomId}\"", body);
        Assert.Contains("\"s3Key\":\"classrooms/x/key.pdf\"", body);
        Assert.Contains("\"fileName\":\"lecture.pdf\"", body);
        Assert.Contains("\"contentType\":\"application/pdf\"", body);
    }

    [Fact]
    public async Task NotifyFileDeleted_sends_delete_to_document_url_with_secret_header()
    {
        var handler = new CapturingHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = CreateClient(handler);

        var fileId = Guid.NewGuid();
        await client.NotifyFileDeletedAsync(fileId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"http://knowledge-service:8080/api/internal/documents/{fileId}", request.Uri!.AbsoluteUri);
        Assert.Equal(Secret, request.SecretHeader);
    }

    [Fact]
    public async Task Retries_on_5xx_then_throws_after_exhausting_attempts()
    {
        var handler = new CapturingHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.NotifyFileUploadedAsync(Guid.NewGuid(), Guid.NewGuid(), "k", "f.pdf", "application/pdf"));

        // Three attempts total (initial + 2 retries).
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Does_not_retry_on_4xx()
    {
        var handler = new CapturingHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.NotifyFileDeletedAsync(Guid.NewGuid()));

        // 4xx is terminal — no retries.
        Assert.Single(handler.Requests);
    }
}
