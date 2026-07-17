using System.Net;
using System.Net.Http.Json;
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

    [Fact]
    public async Task GetIndexingStatus_gets_status_url_with_secret_and_parses_status()
    {
        var fileId = Guid.NewGuid();
        var handler = new CapturingHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { fileId, status = "Processing" }),
        });
        var client = CreateClient(handler);

        var status = await client.GetIndexingStatusAsync(fileId);

        Assert.Equal("Processing", status);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"http://knowledge-service:8080/api/internal/documents/{fileId}/status", request.Uri!.AbsoluteUri);
        Assert.Equal(Secret, request.SecretHeader);
    }

    [Fact]
    public async Task GetIndexingStatus_returns_null_on_404()
    {
        var handler = new CapturingHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient(handler);

        var status = await client.GetIndexingStatusAsync(Guid.NewGuid());

        Assert.Null(status);
        // 404 is terminal — no retries.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetAnswer_posts_question_with_scope_and_secret_and_parses_sources()
    {
        var classroomId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var handler = new CapturingHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // Response mirrors KnowledgeService: includes chunkId/score which must be ignored.
            Content = JsonContent.Create(new
            {
                answer = "Grounded answer [1].",
                sources = new[]
                {
                    new { citation = 1, chunkId = Guid.NewGuid(), documentId, page = 12, slide = (int?)null, section = "Intro", score = 0.87 },
                },
            }),
        });
        var client = CreateClient(handler);

        var result = await client.GetAnswerAsync(classroomId, "What is X?");

        Assert.Equal("Grounded answer [1].", result.Answer);
        var source = Assert.Single(result.Sources);
        Assert.Equal(1, source.Citation);
        Assert.Equal(documentId, source.DocumentId);
        Assert.Equal(12, source.Page);
        Assert.Equal("Intro", source.Section);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://knowledge-service:8080/api/answer", request.Uri!.AbsoluteUri);
        Assert.Equal(Secret, request.SecretHeader);
        // The classroom scope is sent server-side; the secret is a header, never echoed to a client.
        Assert.Contains($"\"classroomId\":\"{classroomId}\"", request.Body!);
        Assert.Contains("\"question\":\"What is X?\"", request.Body!);
    }
}
