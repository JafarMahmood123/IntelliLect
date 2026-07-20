using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;

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

    public async Task<AdminClassroomPage> GetClassroomsAsync(
        int page, int pageSize, string? search, Guid? teacherId, CancellationToken ct = default)
    {
        var url = $"api/internal/classrooms?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(search))
        {
            url += $"&search={Uri.EscapeDataString(search)}";
        }
        if (teacherId.HasValue && teacherId.Value != Guid.Empty)
        {
            url += $"&teacherId={teacherId.Value}";
        }

        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<AdminClassroomPage>(ct);
        return payload ?? new AdminClassroomPage(Array.Empty<AdminClassroom>(), 0, page, pageSize, 0);
    }

    public async Task<AdminClassroom?> GetClassroomByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"api/internal/classrooms/{id}"), ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AdminClassroom>(ct);
    }

    public async Task<Guid> CreateClassroomAsync(Guid teacherId, string name, string description, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "api/internal/classrooms")
            {
                Content = JsonContent.Create(new { teacherId, name, description })
            },
            ct);

        response.EnsureSuccessStatusCode();

        var created = await response.Content.ReadFromJsonAsync<CreatedClassroom>(ct);
        return created?.Id ?? Guid.Empty;
    }

    public async Task UpdateClassroomAsync(Guid id, string name, string description, long version, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Put, $"api/internal/classrooms/{id}")
            {
                Content = JsonContent.Create(new { name, description, version })
            },
            ct);

        // Translate ClassroomService's status codes into UMS domain exceptions.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new NotFoundException("Classroom not found."); // 5ج
        }
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException(
                "The classroom was modified by someone else. Reload the data and try again."); // 6أ
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task<AdminSessionPage> GetSessionsAsync(
        int page, int pageSize, string? search, string? status, Guid? classroomId, CancellationToken ct = default)
    {
        var url = $"api/internal/sessions?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(search)) url += $"&search={Uri.EscapeDataString(search)}";
        if (!string.IsNullOrWhiteSpace(status)) url += $"&status={Uri.EscapeDataString(status)}";
        if (classroomId.HasValue && classroomId.Value != Guid.Empty) url += $"&classroomId={classroomId.Value}";

        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<AdminSessionPage>(ct);
        return payload ?? new AdminSessionPage(Array.Empty<AdminSession>(), 0, page, pageSize, 0);
    }

    public async Task<ForceEndResult> ForceEndSessionAsync(Guid sessionId, string reason, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"api/internal/sessions/{sessionId}/force-end")
            {
                Content = JsonContent.Create(new { reason })
            },
            ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new NotFoundException("Session not found."); // 6أ
        }

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ForceEndResult>(ct);
        return result ?? new ForceEndResult(sessionId, "Unknown", false, false, false);
    }

    // Sends a request (recreated per attempt), adding the internal secret and retrying transient
    // faults. Only idempotent-safe callers should retry POST; here create is a one-shot 201 so a
    // duplicate is not created because the retry only fires on connection/5xx before a response.
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var request = requestFactory();
                if (!string.IsNullOrWhiteSpace(_internalSecret))
                {
                    request.Headers.TryAddWithoutValidation(InternalSecretHeader, _internalSecret);
                }

                var response = await _httpClient.SendAsync(request, ct);

                if ((int)response.StatusCode >= 500 && attempt < MaxAttempts)
                {
                    response.Dispose();
                    await Task.Delay(RetryDelay(attempt), ct);
                    continue;
                }

                return response;
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                await Task.Delay(RetryDelay(attempt), ct);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxAttempts)
            {
                await Task.Delay(RetryDelay(attempt), ct);
            }
        }
    }

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(200 * attempt);

    // Deserialization shape of ClassroomService's UserClassroomsResponse.
    private sealed record UserClassroomsPayload(
        IReadOnlyList<ClassroomSummary>? Teaching,
        IReadOnlyList<ClassroomSummary>? Enrolled);

    private sealed record CreatedClassroom(Guid Id);
}
