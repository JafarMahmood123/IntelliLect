using System.Net.Http.Json;
using ClassroomService.Application.Abstractions;
using ClassroomService.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace ClassroomService.Infrastructure.Services;

public sealed class StreamingInternalClient : IStreamingInternalClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<StreamingInternalClient> _logger;

    public StreamingInternalClient(HttpClient httpClient, ILogger<StreamingInternalClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<bool> CreateStreamAsync(Guid sessionId, Guid classroomId, Guid teacherId, StudentParticipationMode participationMode, bool recordingEnabled, CancellationToken ct)
    {
        try
        {
            var request = new { SessionId = sessionId, ClassroomId = classroomId, TeacherId = teacherId, ParticipationMode = participationMode, RecordingEnabled = recordingEnabled };
            var response = await _httpClient.PostAsJsonAsync("api/internal/streams", request, ct);

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reach Streaming Service for Session {SessionId}", sessionId);
            return false;
        }
    }

    public async Task<bool> EndStreamAsync(Guid sessionId, CancellationToken ct = default)
    {
        try
        {
            // Reuses StreamingService's existing end path (stop egress, notify assistant, close room).
            // A 404 means no live stream exists for this session (nothing to close) — treat as success.
            var response = await _httpClient.PostAsync(
                $"api/internal/streams/{sessionId}/end", content: null, ct);

            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to end Streaming room for Session {SessionId}", sessionId);
            return false;
        }
    }
}