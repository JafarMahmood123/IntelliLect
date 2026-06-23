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

    public async Task<bool> CreateStreamAsync(Guid sessionId, Guid classroomId, Guid teacherId, StudentParticipationMode participationMode, CancellationToken ct)
    {
        try
        {
            var request = new { SessionId = sessionId, ClassroomId = classroomId, TeacherId = teacherId, ParticipationMode = participationMode };
            var response = await _httpClient.PostAsJsonAsync("http://streaming-service:8080/api/internal/streams", request, ct);

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reach Streaming Service for Session {SessionId}", sessionId);
            return false;
        }
    }
}