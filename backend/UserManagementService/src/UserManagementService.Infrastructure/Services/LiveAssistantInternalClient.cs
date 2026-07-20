using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using UserManagementService.Application.Abstractions;

namespace UserManagementService.Infrastructure.Services;

/// <summary>
/// Typed HttpClient reading LiveAssistantService's active-session registry
/// (<c>GET api/internal/sessions</c>) so the monitor can show whether the AI assistant is
/// running for a live session. Secured by the shared <c>X-Internal-Secret</c> header.
/// Throws on failure so the caller can degrade (alternate path 4أ).
/// </summary>
public sealed class LiveAssistantInternalClient : ILiveAssistantInternalClient
{
    private const string InternalSecretHeader = "X-Internal-Secret";

    private readonly HttpClient _httpClient;
    private readonly string _internalSecret;

    public LiveAssistantInternalClient(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _internalSecret = configuration["LiveAssistant:InternalApiSecret"] ?? string.Empty;
    }

    public async Task<IReadOnlyCollection<Guid>> GetActiveSessionIdsAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/internal/sessions");
        if (!string.IsNullOrWhiteSpace(_internalSecret))
        {
            request.Headers.TryAddWithoutValidation(InternalSecretHeader, _internalSecret);
        }

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ActiveSessionsPayload>(ct);

        // LiveAssistant returns ids as strings; ignore anything unparsable rather than failing.
        return (payload?.ActiveSessions ?? Array.Empty<string>())
            .Select(id => Guid.TryParse(id, out var parsed) ? parsed : (Guid?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();
    }

    private sealed record ActiveSessionsPayload(IReadOnlyList<string>? ActiveSessions, int Count);
}
