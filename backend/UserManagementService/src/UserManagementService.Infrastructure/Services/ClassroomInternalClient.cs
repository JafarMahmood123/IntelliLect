using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using UserManagementService.Application.Abstractions;

namespace UserManagementService.Infrastructure.Services;

/// <summary>
/// Typed HttpClient that reads a user's classroom memberships from ClassroomService's
/// internal endpoint (<c>GET api/internal/users/{userId}/classrooms</c>). Mirrors the
/// internal-client convention used elsewhere in the stack: sends the shared
/// <c>X-Internal-Secret</c> header when configured, and retries transient faults
/// (5xx / timeouts / connection failures). After the retries are exhausted it throws,
/// so the caller can fall back to showing the user without their memberships (7ب).
/// </summary>
public sealed class ClassroomInternalClient : IClassroomInternalClient
{
    private const string InternalSecretHeader = "X-Internal-Secret";
    private const int MaxAttempts = 3;

    private readonly HttpClient _httpClient;
    private readonly string _internalSecret;

    public ClassroomInternalClient(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _internalSecret = configuration["ClassroomService:InternalApiSecret"] ?? string.Empty;
    }

    public async Task<UserClassrooms> GetUserClassroomsAsync(Guid userId, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get, $"api/internal/users/{userId}/classrooms");

                if (!string.IsNullOrWhiteSpace(_internalSecret))
                {
                    request.Headers.TryAddWithoutValidation(InternalSecretHeader, _internalSecret);
                }

                using var response = await _httpClient.SendAsync(request, ct);

                // Retry server-side faults; client-side errors (4xx) are not retryable.
                if ((int)response.StatusCode >= 500 && attempt < MaxAttempts)
                {
                    await Task.Delay(RetryDelay(attempt), ct);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                var payload = await response.Content.ReadFromJsonAsync<UserClassroomsPayload>(ct);
                if (payload is null)
                {
                    return UserClassrooms.Empty;
                }

                return new UserClassrooms(
                    payload.Teaching ?? Array.Empty<ClassroomSummary>(),
                    payload.Enrolled ?? Array.Empty<ClassroomSummary>());
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                await Task.Delay(RetryDelay(attempt), ct);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxAttempts)
            {
                // Timeout (not a caller-requested cancellation): retry.
                await Task.Delay(RetryDelay(attempt), ct);
            }
        }
    }

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(200 * attempt);

    // Deserialization shape of ClassroomService's UserClassroomsResponse.
    private sealed record UserClassroomsPayload(
        IReadOnlyList<ClassroomSummary>? Teaching,
        IReadOnlyList<ClassroomSummary>? Enrolled);
}
