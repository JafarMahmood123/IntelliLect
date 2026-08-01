using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ClassroomService.Application.Abstractions;
using ClassroomService.Application.Exceptions;
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

    public async Task<GeneratedQuizDto> GenerateQuizAsync(
        Guid sessionId,
        Guid classroomId,
        int questionCount,
        int minOptions,
        int maxOptions,
        CancellationToken ct = default)
    {
        var body = new GenerateQuizRequest(classroomId, questionCount, minOptions, maxOptions);

        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"api/internal/sessions/{sessionId}/quiz")
            {
                Content = JsonContent.Create(body),
            },
            "quiz generation",
            sessionId,
            ct,
            // Generation runs a language model over the transcript: tens of seconds is normal, not
            // a fault, so it gets its own budget instead of the transcript calls' short one.
            timeout: TimeSpan.FromSeconds(
                _options.GenerationTimeoutSeconds > 0 ? _options.GenerationTimeoutSeconds : 120),
            // NOT retried. A retry here would re-run the model — another minute of a teacher
            // standing in front of a class, and another call's cost, to repeat work that just
            // failed. Better to report it and let them press the button again if they want to.
            maxAttempts: 1);

        // 409: the session has produced no idea yet. Nothing is broken and a retry now would fail
        // the same way, so it is reported as a conflict rather than a transient fault.
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ConflictException(
                "There is nothing to build a quiz from yet — the assistant has not transcribed "
                + "enough of this session. Keep teaching and try again in a moment.");
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "LiveAssistant quiz generation for {SessionId} returned {StatusCode}.",
                sessionId, (int)response.StatusCode);
            throw new ServiceUnavailableException(
                "The teaching assistant could not generate a quiz right now. Please try again.");
        }

        var quiz = await response.Content.ReadFromJsonAsync<GenerateQuizResponse>(ct);
        if (quiz is null || quiz.Questions.Count == 0)
        {
            throw new ServiceUnavailableException(
                "The teaching assistant returned no questions. Please try again.");
        }

        return new GeneratedQuizDto(
            quiz.Title ?? string.Empty,
            quiz.Grounded,
            quiz.Questions
                .Select(q => new GeneratedQuestionDto(
                    q.Text ?? string.Empty,
                    q.Options
                        .Select(o => new GeneratedOptionDto(o.Text ?? string.Empty, o.IsCorrect))
                        .ToList()))
                .ToList());
    }

    // Sends a request (recreated per attempt) with the internal secret, retrying transient faults.
    // Terminal statuses (including 404) are returned to the caller to interpret.
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        string operation,
        Guid id,
        CancellationToken ct,
        TimeSpan? timeout = null,
        int maxAttempts = MaxAttempts)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            // Per-request budget. The client-wide timeout covers the longest operation, so each
            // call narrows it to what that call should actually be allowed to take.
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(
                _options.TimeoutSeconds > 0 ? _options.TimeoutSeconds : 10));

            try
            {
                using var request = requestFactory();
                if (!string.IsNullOrWhiteSpace(_options.InternalApiSecret))
                {
                    request.Headers.TryAddWithoutValidation(InternalSecretHeader, _options.InternalApiSecret);
                }

                response = await _httpClient.SendAsync(request, attemptCts.Token);
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                _logger.LogWarning(
                    "LiveAssistant {Operation} for {Id} could not connect; retry {Attempt}/{Max}.",
                    operation, id, attempt, maxAttempts);
                await Task.Delay(RetryDelay(attempt), ct);
                continue;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < maxAttempts)
            {
                _logger.LogWarning(
                    "LiveAssistant {Operation} for {Id} timed out; retry {Attempt}/{Max}.",
                    operation, id, attempt, maxAttempts);
                await Task.Delay(RetryDelay(attempt), ct);
                continue;
            }

            if ((int)response.StatusCode >= 500 && attempt < maxAttempts)
            {
                response.Dispose();
                _logger.LogWarning(
                    "LiveAssistant {Operation} for {Id} returned {StatusCode}; retry {Attempt}/{Max}.",
                    operation, id, (int)response.StatusCode, attempt, maxAttempts);
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

    private sealed record GenerateQuizRequest(
        [property: JsonPropertyName("classroomId")] Guid ClassroomId,
        [property: JsonPropertyName("questionCount")] int QuestionCount,
        [property: JsonPropertyName("minOptions")] int MinOptions,
        [property: JsonPropertyName("maxOptions")] int MaxOptions);

    private sealed record GenerateQuizResponse(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("grounded")] bool Grounded,
        [property: JsonPropertyName("questions")] IReadOnlyList<GeneratedQuestionResponse> Questions);

    private sealed record GeneratedQuestionResponse(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("options")] IReadOnlyList<GeneratedOptionResponse> Options);

    private sealed record GeneratedOptionResponse(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("isCorrect")] bool IsCorrect);
}
