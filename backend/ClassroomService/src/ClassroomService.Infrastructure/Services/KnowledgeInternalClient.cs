using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ClassroomService.Application.Abstractions;
using ClassroomService.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassroomService.Infrastructure.Services;

/// <summary>
/// Typed HttpClient that notifies KnowledgeService of file uploads/deletes so they get
/// indexed. Mirrors <see cref="StreamingInternalClient"/>. Sends the shared internal
/// secret (matching KnowledgeService's INTERNAL_API_SECRET) on every request and retries
/// transient faults (5xx / timeouts / connection failures). After the retries are
/// exhausted it throws, so the caller's non-fatal wrapper can log and carry on.
/// </summary>
public sealed class KnowledgeInternalClient : IKnowledgeInternalClient
{
    // KnowledgeService authenticates internal calls via this header (its
    // require_internal_secret dependency reads "X-Internal-Secret").
    private const string InternalSecretHeader = "X-Internal-Secret";
    private const string IngestPath = "api/internal/documents/ingest";
    private const string DocumentsPath = "api/internal/documents";
    private const int MaxAttempts = 3;

    private readonly HttpClient _httpClient;
    private readonly KnowledgeServiceOptions _options;
    private readonly ILogger<KnowledgeInternalClient> _logger;

    public KnowledgeInternalClient(
        HttpClient httpClient,
        IOptions<KnowledgeServiceOptions> options,
        ILogger<KnowledgeInternalClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public Task NotifyFileUploadedAsync(
        Guid fileId,
        Guid classroomId,
        string s3Key,
        string fileName,
        string contentType,
        CancellationToken ct = default)
    {
        var body = new IngestDocumentRequest(fileId, classroomId, s3Key, fileName, contentType);
        return SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, IngestPath)
            {
                Content = JsonContent.Create(body)
            },
            "ingest",
            fileId,
            ct);
    }

    public Task NotifyFileDeletedAsync(Guid fileId, CancellationToken ct = default)
    {
        return SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, $"{DocumentsPath}/{fileId}"),
            "delete",
            fileId,
            ct);
    }

    private async Task SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        string operation,
        Guid fileId,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = requestFactory();
                if (!string.IsNullOrWhiteSpace(_options.InternalApiSecret))
                {
                    request.Headers.TryAddWithoutValidation(InternalSecretHeader, _options.InternalApiSecret);
                }

                response = await _httpClient.SendAsync(request, ct);
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                _logger.LogWarning(
                    "KnowledgeService {Operation} for file {FileId} could not connect; retry {Attempt}/{Max}.",
                    operation, fileId, attempt, MaxAttempts);
                await Task.Delay(RetryDelay(attempt), ct);
                continue;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxAttempts)
            {
                // An HttpClient timeout (not caller cancellation) is transient.
                _logger.LogWarning(
                    "KnowledgeService {Operation} for file {FileId} timed out; retry {Attempt}/{Max}.",
                    operation, fileId, attempt, MaxAttempts);
                await Task.Delay(RetryDelay(attempt), ct);
                continue;
            }

            // Evaluate the response OUTSIDE the transient-exception catch, so a terminal
            // status (4xx) is not mistaken for a connection failure and retried.
            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "KnowledgeService {Operation} succeeded for file {FileId} ({StatusCode}).",
                        operation, fileId, (int)response.StatusCode);
                    return;
                }

                // Retry transient server errors only; 4xx is terminal.
                if ((int)response.StatusCode >= 500 && attempt < MaxAttempts)
                {
                    _logger.LogWarning(
                        "KnowledgeService {Operation} for file {FileId} returned {StatusCode}; retry {Attempt}/{Max}.",
                        operation, fileId, (int)response.StatusCode, attempt, MaxAttempts);
                    await Task.Delay(RetryDelay(attempt), ct);
                    continue;
                }

                throw new HttpRequestException(
                    $"KnowledgeService {operation} for file {fileId} failed with status {(int)response.StatusCode}.");
            }
        }
    }

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(200 * attempt);

    /// <summary>Matches KnowledgeService's ingest DTO (camelCase aliases).</summary>
    private sealed record IngestDocumentRequest(
        [property: JsonPropertyName("fileId")] Guid FileId,
        [property: JsonPropertyName("classroomId")] Guid ClassroomId,
        [property: JsonPropertyName("s3Key")] string S3Key,
        [property: JsonPropertyName("fileName")] string FileName,
        [property: JsonPropertyName("contentType")] string ContentType);
}
