using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamingService.Application.Abstractions;
using StreamingService.Infrastructure.Configuration;

namespace StreamingService.Infrastructure.Services;

/// <summary>
/// Typed HttpClient that notifies LiveAssistantService of session start/stop. Mirrors
/// ClassroomService's internal clients: sends the shared internal secret (matching the
/// assistant's INTERNAL_API_SECRET) on every request. A single attempt — the assistant
/// is a non-critical enhancement, so this does NOT retry and keeps session start/stop
/// snappy; on failure it throws so the caller's non-fatal wrapper logs a warning and
/// carries on.
/// </summary>
public sealed class LiveAssistantInternalClient : ILiveAssistantInternalClient
{
    // LiveAssistantService authenticates internal calls via this header
    // (its require_internal_secret dependency reads "X-Internal-Secret").
    private const string InternalSecretHeader = "X-Internal-Secret";
    private const string SessionsPath = "api/internal/sessions";

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

    public Task NotifySessionStartedAsync(
        Guid sessionId,
        Guid classroomId,
        string roomName,
        string teacherIdentity,
        CancellationToken ct = default)
    {
        var body = new StartSessionRequest(sessionId, classroomId, roomName, teacherIdentity);
        return SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"{SessionsPath}/start")
            {
                Content = JsonContent.Create(body)
            },
            "start",
            sessionId,
            ct);
    }

    public Task NotifySessionEndedAsync(Guid sessionId, CancellationToken ct = default)
    {
        return SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"{SessionsPath}/{sessionId}/stop"),
            "stop",
            sessionId,
            ct);
    }

    private async Task SendAsync(
        Func<HttpRequestMessage> requestFactory,
        string operation,
        Guid sessionId,
        CancellationToken ct)
    {
        using var request = requestFactory();
        if (!string.IsNullOrWhiteSpace(_options.InternalApiSecret))
        {
            request.Headers.TryAddWithoutValidation(InternalSecretHeader, _options.InternalApiSecret);
        }

        using var response = await _httpClient.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation(
                "LiveAssistant {Operation} succeeded for session {SessionId} ({StatusCode}).",
                operation, sessionId, (int)response.StatusCode);
            return;
        }

        throw new HttpRequestException(
            $"LiveAssistant {operation} for session {sessionId} failed with status {(int)response.StatusCode}.");
    }

    /// <summary>Matches LiveAssistantService's start DTO (camelCase aliases).</summary>
    private sealed record StartSessionRequest(
        [property: JsonPropertyName("sessionId")] Guid SessionId,
        [property: JsonPropertyName("classroomId")] Guid ClassroomId,
        [property: JsonPropertyName("roomName")] string RoomName,
        [property: JsonPropertyName("teacherIdentity")] string TeacherIdentity);
}
