using System.Net.Http.Json;
using UserManagementService.Application.Abstractions;

namespace UserManagementService.Infrastructure.Services;

/// <summary>
/// Typed HttpClient reading StreamingService's live-stream snapshot
/// (<c>GET api/internal/streams/live</c>) for the super-admin live-session monitor.
/// StreamingService's internal endpoints are network-isolated (not proxied by nginx) and
/// currently carry no shared secret, so none is sent. Throws on failure so the caller can
/// degrade to stored data (alternate path 4أ).
/// </summary>
public sealed class StreamingInternalClient : IStreamingInternalClient
{
    private readonly HttpClient _httpClient;

    public StreamingInternalClient(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<IReadOnlyList<LiveStreamSnapshot>> GetLiveStreamsAsync(CancellationToken ct = default)
    {
        using var response = await _httpClient.GetAsync("api/internal/streams/live", ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<LiveStreamsPayload>(ct);
        return payload?.Items ?? Array.Empty<LiveStreamSnapshot>();
    }

    private sealed record LiveStreamsPayload(IReadOnlyList<LiveStreamSnapshot>? Items);
}
