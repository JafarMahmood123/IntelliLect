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

    public async Task<ClassroomTeacherChange> ChangeClassroomTeacherAsync(
        Guid id, Guid newTeacherId, long version, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Put, $"api/internal/classrooms/{id}/teacher")
            {
                Content = JsonContent.Create(new { newTeacherId, version })
            },
            ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new NotFoundException("Classroom not found."); // 3أ
        }
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException(
                "The classroom has a live session or was modified by someone else. End the session or reload and try again."); // 3ب / concurrency
        }

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ClassroomTeacherChange>(ct);
        return result ?? new ClassroomTeacherChange(false, Guid.Empty, newTeacherId, string.Empty);
    }

    public async Task<ClassroomDeletionImpact?> GetClassroomDeletionImpactAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"api/internal/classrooms/{id}/deletion-impact"), ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null; // 5أ
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClassroomDeletionImpact>(ct);
    }

    public async Task<ClassroomDeletionResult> DeleteClassroomAsync(Guid id, string reason, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, $"api/internal/classrooms/{id}")
            {
                Content = JsonContent.Create(new { reason })
            },
            ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new NotFoundException("Classroom not found."); // 5أ
        }
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException(
                "The classroom has a live session. End the session before deleting the classroom."); // 5ب
        }

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ClassroomDeletionResult>(ct);
        return result ?? new ClassroomDeletionResult(id, 0, 0, 0, 0, 0);
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

    public async Task<SessionDeletionImpact?> GetSessionDeletionImpactAsync(Guid sessionId, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"api/internal/sessions/{sessionId}/deletion-impact"), ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null; // 5أ
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SessionDeletionImpact>(ct);
    }

    public async Task<SessionDeletionResult> DeleteSessionAsync(Guid sessionId, string reason, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, $"api/internal/sessions/{sessionId}")
            {
                Content = JsonContent.Create(new { reason })
            },
            ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new NotFoundException("Session not found."); // 5أ
        }
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException(
                "The session is live. End the session before deleting it."); // 5ب
        }

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<SessionDeletionResult>(ct);
        return result ?? new SessionDeletionResult(sessionId, false, false, false);
    }

    public async Task<AdminFilePage> GetFilesAsync(
        int page, int pageSize, string? search, Guid? classroomId, CancellationToken ct = default)
    {
        var url = $"api/internal/files?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(search)) url += $"&search={Uri.EscapeDataString(search)}";
        if (classroomId.HasValue && classroomId.Value != Guid.Empty) url += $"&classroomId={classroomId.Value}";

        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<AdminFilePage>(ct);
        return payload ?? new AdminFilePage(Array.Empty<AdminFile>(), 0, page, pageSize);
    }

    public async Task<IReadOnlyList<AdminFile>> GetFilesByIdsAsync(
        IReadOnlyCollection<Guid> fileIds, CancellationToken ct = default)
    {
        if (fileIds.Count == 0)
        {
            return Array.Empty<AdminFile>();
        }

        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "api/internal/files/by-ids")
            {
                Content = JsonContent.Create(new { fileIds })
            },
            ct);

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<List<AdminFile>>(ct);
        return payload ?? new List<AdminFile>();
    }

    public async Task<IReadOnlyList<ClassroomName>> GetClassroomNamesAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return Array.Empty<ClassroomName>();
        }

        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "api/internal/classrooms/names")
            {
                Content = JsonContent.Create(new { ids })
            },
            ct);

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<List<ClassroomName>>(ct);
        return payload ?? new List<ClassroomName>();
    }

    public async Task<FileDeletionResult> DeleteFileAsync(Guid fileId, string reason, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, $"api/internal/files/{fileId}")
            {
                Content = JsonContent.Create(new { reason })
            },
            ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new NotFoundException("File not found."); // 7أ
        }

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<FileDeletionResult>(ct);
        return result ?? new FileDeletionResult(fileId, false, false);
    }

    public async Task<ClassroomMembersData> GetClassroomMembersAsync(Guid classroomId, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"api/internal/classrooms/{classroomId}/members"), ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new NotFoundException("Classroom not found."); // 5أ
        }

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ClassroomMembersData>(ct);
        return payload ?? new ClassroomMembersData(classroomId, string.Empty, Guid.Empty, Array.Empty<ClassroomMemberRow>());
    }

    public async Task<MemberChangeResult> AddClassroomMemberAsync(Guid classroomId, Guid studentId, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"api/internal/classrooms/{classroomId}/members")
            {
                Content = JsonContent.Create(new { studentId })
            },
            ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new NotFoundException("Classroom not found."); // 5أ
        }

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<MemberChangeResult>(ct);
        return result ?? new MemberChangeResult(false, classroomId, string.Empty, studentId);
    }

    public async Task<MemberChangeResult> RemoveClassroomMemberAsync(Guid classroomId, Guid studentId, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, $"api/internal/classrooms/{classroomId}/members/{studentId}"), ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new NotFoundException("Classroom or membership not found."); // 5أ / 5د
        }
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException(
                "The classroom teacher cannot be removed here. Use teacher reassignment to change the owner."); // 5هـ
        }

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<MemberChangeResult>(ct);
        return result ?? new MemberChangeResult(true, classroomId, string.Empty, studentId);
    }

    public async Task<AdminOutputPage> GetOutputsAsync(
        int page, int pageSize, string? search, string? type, string? status, Guid? classroomId, CancellationToken ct = default)
    {
        var url = $"api/internal/outputs?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(search)) url += $"&search={Uri.EscapeDataString(search)}";
        if (!string.IsNullOrWhiteSpace(type)) url += $"&type={Uri.EscapeDataString(type)}";
        if (!string.IsNullOrWhiteSpace(status)) url += $"&status={Uri.EscapeDataString(status)}";
        if (classroomId.HasValue && classroomId.Value != Guid.Empty) url += $"&classroomId={classroomId.Value}";

        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<AdminOutputPage>(ct);
        return payload ?? new AdminOutputPage(Array.Empty<AdminOutput>(), 0, page, pageSize);
    }

    public Task<OutputDeletionResult> DeleteRecordingAsync(Guid recordingId, string reason, CancellationToken ct = default)
        => DeleteOutputAsync($"api/internal/outputs/recordings/{recordingId}", recordingId, "Recording", reason, ct);

    public Task<OutputDeletionResult> DeleteSummaryAsync(Guid summaryId, string reason, CancellationToken ct = default)
        => DeleteOutputAsync($"api/internal/outputs/summaries/{summaryId}", summaryId, "Summary", reason, ct);

    private async Task<OutputDeletionResult> DeleteOutputAsync(
        string path, Guid id, string type, string reason, CancellationToken ct)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, path)
            {
                Content = JsonContent.Create(new { reason })
            },
            ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new NotFoundException("Output not found."); // 5أ
        }
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException(
                "The output's session is live. End the session before deleting the output."); // 5ب
        }

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OutputDeletionResult>(ct);
        return result ?? new OutputDeletionResult(id, type, false, false);
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
