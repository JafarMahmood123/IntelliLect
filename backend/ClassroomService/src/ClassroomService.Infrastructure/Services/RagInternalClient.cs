using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ClassroomService.Application.Abstractions;
using ClassroomService.Application.Models;
using ClassroomService.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassroomService.Infrastructure.Services;

/// <summary>
/// Typed HttpClient that notifies RagService of file uploads/deletes so they get
/// indexed. Mirrors <see cref="StreamingInternalClient"/>. Sends the shared internal
/// secret (matching RagService's INTERNAL_API_SECRET) on every request and retries
/// transient faults (5xx / timeouts / connection failures). After the retries are
/// exhausted it throws, so the caller's non-fatal wrapper can log and carry on.
/// </summary>
public sealed class RagInternalClient : IRagInternalClient
{
    // RagService authenticates internal calls via this header (its
    // require_internal_secret dependency reads "X-Internal-Secret").
    private const string InternalSecretHeader = "X-Internal-Secret";
    private const string IngestPath = "api/internal/documents/ingest";
    private const string DocumentsPath = "api/internal/documents";
    private const string AnswerPath = "api/answer";
    private const int MaxAttempts = 3;

    private readonly HttpClient _httpClient;
    private readonly RagServiceOptions _options;
    private readonly ILogger<RagInternalClient> _logger;

    public RagInternalClient(
        HttpClient httpClient,
        IOptions<RagServiceOptions> options,
        ILogger<RagInternalClient> logger)
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
        long sizeBytes,
        CancellationToken ct = default)
    {
        var body = new IngestDocumentRequest(fileId, classroomId, s3Key, fileName, contentType, sizeBytes);
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

    public Task DeIndexClassroomAsync(Guid classroomId, CancellationToken ct = default)
    {
        // Reuses the retry-then-throw send helper. The "fileId" slot carries the classroom id purely
        // for log correlation — the route is classroom-scoped.
        return SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, $"{DocumentsPath}/classrooms/{classroomId}"),
            "classroom de-index",
            classroomId,
            ct);
    }

    public async Task<string?> GetIndexingStatusAsync(Guid fileId, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{DocumentsPath}/{fileId}/status");
                if (!string.IsNullOrWhiteSpace(_options.InternalApiSecret))
                {
                    request.Headers.TryAddWithoutValidation(InternalSecretHeader, _options.InternalApiSecret);
                }

                response = await _httpClient.SendAsync(request, ct);
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                _logger.LogWarning(
                    "RagService status for file {FileId} could not connect; retry {Attempt}/{Max}.",
                    fileId, attempt, MaxAttempts);
                await Task.Delay(RetryDelay(attempt), ct);
                continue;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxAttempts)
            {
                _logger.LogWarning(
                    "RagService status for file {FileId} timed out; retry {Attempt}/{Max}.",
                    fileId, attempt, MaxAttempts);
                await Task.Delay(RetryDelay(attempt), ct);
                continue;
            }

            using (response)
            {
                // No document registered yet -> caller treats as still-pending.
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadFromJsonAsync<DocumentStatusResponse>(ct);
                    return body?.Status;
                }

                if ((int)response.StatusCode >= 500 && attempt < MaxAttempts)
                {
                    _logger.LogWarning(
                        "RagService status for file {FileId} returned {StatusCode}; retry {Attempt}/{Max}.",
                        fileId, (int)response.StatusCode, attempt, MaxAttempts);
                    await Task.Delay(RetryDelay(attempt), ct);
                    continue;
                }

                throw new HttpRequestException(
                    $"RagService status for file {fileId} failed with status {(int)response.StatusCode}.");
            }
        }
    }

    public async Task<KnowledgeAnswerResult> GetAnswerAsync(
        Guid classroomId, string question, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                var body = new AnswerRequest(classroomId, question);
                using var request = new HttpRequestMessage(HttpMethod.Post, AnswerPath)
                {
                    Content = JsonContent.Create(body),
                };
                if (!string.IsNullOrWhiteSpace(_options.InternalApiSecret))
                {
                    request.Headers.TryAddWithoutValidation(InternalSecretHeader, _options.InternalApiSecret);
                }

                response = await _httpClient.SendAsync(request, ct);
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                _logger.LogWarning(
                    "RagService answer for classroom {ClassroomId} could not connect; retry {Attempt}/{Max}.",
                    classroomId, attempt, MaxAttempts);
                await Task.Delay(RetryDelay(attempt), ct);
                continue;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxAttempts)
            {
                _logger.LogWarning(
                    "RagService answer for classroom {ClassroomId} timed out; retry {Attempt}/{Max}.",
                    classroomId, attempt, MaxAttempts);
                await Task.Delay(RetryDelay(attempt), ct);
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadFromJsonAsync<AnswerResponseBody>(ct)
                        ?? new AnswerResponseBody(string.Empty, new List<AnswerSourceBody>());
                    var sources = (body.Sources ?? new List<AnswerSourceBody>())
                        .Select(s => new KnowledgeAnswerSource(s.Citation, s.DocumentId, s.Page, s.Slide, s.Section))
                        .ToList();
                    return new KnowledgeAnswerResult(body.Answer ?? string.Empty, sources);
                }

                if ((int)response.StatusCode >= 500 && attempt < MaxAttempts)
                {
                    _logger.LogWarning(
                        "RagService answer for classroom {ClassroomId} returned {StatusCode}; retry {Attempt}/{Max}.",
                        classroomId, (int)response.StatusCode, attempt, MaxAttempts);
                    await Task.Delay(RetryDelay(attempt), ct);
                    continue;
                }

                throw new HttpRequestException(
                    $"RagService answer for classroom {classroomId} failed with status {(int)response.StatusCode}.");
            }
        }
    }

    public async Task<bool> TriggerSummaryAsync(Guid sessionId, Guid classroomId, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"api/internal/sessions/{sessionId}/summarize")
            {
                Content = JsonContent.Create(new SummarizeRequest(classroomId)),
            };
            if (!string.IsNullOrWhiteSpace(_options.InternalApiSecret))
            {
                request.Headers.TryAddWithoutValidation(InternalSecretHeader, _options.InternalApiSecret);
            }

            using var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            _logger.LogWarning(
                "RagService summarize for session {SessionId} returned {StatusCode}.",
                sessionId, (int)response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            // Best-effort (7أ): a summary-trigger failure must not fail the force-end.
            _logger.LogWarning(ex, "RagService summarize for session {SessionId} could not be reached.", sessionId);
            return false;
        }
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
                    "RagService {Operation} for file {FileId} could not connect; retry {Attempt}/{Max}.",
                    operation, fileId, attempt, MaxAttempts);
                await Task.Delay(RetryDelay(attempt), ct);
                continue;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxAttempts)
            {
                // An HttpClient timeout (not caller cancellation) is transient.
                _logger.LogWarning(
                    "RagService {Operation} for file {FileId} timed out; retry {Attempt}/{Max}.",
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
                        "RagService {Operation} succeeded for file {FileId} ({StatusCode}).",
                        operation, fileId, (int)response.StatusCode);
                    return;
                }

                // Retry transient server errors only; 4xx is terminal.
                if ((int)response.StatusCode >= 500 && attempt < MaxAttempts)
                {
                    _logger.LogWarning(
                        "RagService {Operation} for file {FileId} returned {StatusCode}; retry {Attempt}/{Max}.",
                        operation, fileId, (int)response.StatusCode, attempt, MaxAttempts);
                    await Task.Delay(RetryDelay(attempt), ct);
                    continue;
                }

                throw new HttpRequestException(
                    $"RagService {operation} for file {fileId} failed with status {(int)response.StatusCode}.");
            }
        }
    }

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(200 * attempt);

    /// <summary>Matches RagService's status read model ({ fileId, status }).</summary>
    private sealed record DocumentStatusResponse(
        [property: JsonPropertyName("fileId")] Guid FileId,
        [property: JsonPropertyName("status")] string Status);

    /// <summary>Matches RagService's answer request ({ classroomId, question }).</summary>
    private sealed record AnswerRequest(
        [property: JsonPropertyName("classroomId")] Guid ClassroomId,
        [property: JsonPropertyName("question")] string Question);

    /// <summary>Matches RagService's summarize request ({ classroomId }).</summary>
    private sealed record SummarizeRequest(
        [property: JsonPropertyName("classroomId")] Guid ClassroomId);

    /// <summary>Subset of RagService's answer response we forward (ignores chunkId/score).</summary>
    private sealed record AnswerResponseBody(
        [property: JsonPropertyName("answer")] string Answer,
        [property: JsonPropertyName("sources")] List<AnswerSourceBody> Sources);

    private sealed record AnswerSourceBody(
        [property: JsonPropertyName("citation")] int Citation,
        [property: JsonPropertyName("documentId")] Guid DocumentId,
        [property: JsonPropertyName("page")] int? Page,
        [property: JsonPropertyName("slide")] int? Slide,
        [property: JsonPropertyName("section")] string? Section);

    /// <summary>Matches RagService's ingest DTO (camelCase aliases).</summary>
    private sealed record IngestDocumentRequest(
        [property: JsonPropertyName("fileId")] Guid FileId,
        [property: JsonPropertyName("classroomId")] Guid ClassroomId,
        [property: JsonPropertyName("s3Key")] string S3Key,
        [property: JsonPropertyName("fileName")] string FileName,
        [property: JsonPropertyName("contentType")] string ContentType,
        [property: JsonPropertyName("sizeBytes")] long SizeBytes);
}
