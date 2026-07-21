using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ClassroomService.Application.Abstractions;
using ClassroomService.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassroomService.Infrastructure.Services;

/// <summary>
/// Typed HttpClient for LiveAssistantService's internal transcript endpoints. Sends the shared
/// internal secret (matching LiveAssistantService's INTERNAL_API_SECRET) on every request and
/// retries transient faults (5xx / timeouts / connection failures). After the retries are
/// exhausted it throws, so a transcript-delete failure halts the session deletion (6ب), while a
/// 404 (no transcript) is treated as success (6أ).
/// </summary>
public sealed class LiveAssistantInternalClient : ILiveAssistantInternalClient
{
    private const string InternalSecretHeader = "X-Internal-Secret";
    private const int MaxAttempts = 3;

    private readonly HttpClient _httpClient;
    private readonly LiveAssistantOptions _options;
    private readonly ILogger<LiveAssistantInternalClient> _logger;

    public LiveAssistantInternalClient(
        HttpClient httpClient,
        IOptions<LiveAssistantOptions> options,
        ILogger<LiveAssistantInternalClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int?> GetTranscriptSegmentCountAsync(Guid sessionId, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"api/internal/sessions/{sessionId}/transcript"),
            "transcript status",
            sessionId,
            ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null; // no transcript for this session
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TranscriptResponse>(ct);
        return body?.SegmentCount ?? 0;
    }

    public async Task DeleteSessionTranscriptAsync(Guid sessionId, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, $"api/internal/sessions/{sessionId}/transcript"),
            "transcript delete",
            sessionId,
            ct);

        // The endpoint is idempotent (204 even when there was nothing to delete).
        response.EnsureSuccessStatusCode();
    }

    public async Task<int> DeleteClassroomTranscriptsAsync(Guid classroomId, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, $"api/internal/classrooms/{classroomId}/transcripts"),
            "classroom transcripts delete",
            classroomId,
            ct);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<DeleteClassroomTranscriptsResponse>(ct);
        return body?.TranscriptsDeleted ?? 0;
    }

    // Sends a request (recreated per attempt) with the internal secret, retrying transient faults.
    // Terminal statuses (including 404) are returned to the caller to interpret.
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory, string operation, Guid id, CancellationToken ct)
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
                    "LiveAssistant {Operation} for {Id} could not connect; retry {Attempt}/{Max}.",
                    operation, id, attempt, MaxAttempts);
                await Task.Delay(RetryDelay(attempt), ct);
                continue;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxAttempts)
            {
                _logger.LogWarning(
                    "LiveAssistant {Operation} for {Id} timed out; retry {Attempt}/{Max}.",
                    operation, id, attempt, MaxAttempts);
                await Task.Delay(RetryDelay(attempt), ct);
                continue;
            }

            if ((int)response.StatusCode >= 500 && attempt < MaxAttempts)
            {
                response.Dispose();
                _logger.LogWarning(
                    "LiveAssistant {Operation} for {Id} returned {StatusCode}; retry {Attempt}/{Max}.",
                    operation, id, (int)response.StatusCode, attempt, MaxAttempts);
                await Task.Delay(RetryDelay(attempt), ct);
                continue;
            }

            return response;
        }
    }

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(200 * attempt);

    private sealed record TranscriptResponse(
        [property: JsonPropertyName("segmentCount")] int SegmentCount);

    private sealed record DeleteClassroomTranscriptsResponse(
        [property: JsonPropertyName("transcriptsDeleted")] int TranscriptsDeleted);
}
