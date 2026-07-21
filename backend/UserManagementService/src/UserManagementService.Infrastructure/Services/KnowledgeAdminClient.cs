using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;

namespace UserManagementService.Infrastructure.Services;

/// <summary>
/// Typed HttpClient to KnowledgeService's internal admin endpoints (list/status-batch/detail/stats/
/// reindex). Sends the shared <c>X-Internal-Secret</c> and retries transient faults; terminal
/// statuses (404/409/400/503) are translated into UMS domain exceptions the controllers map to HTTP.
/// </summary>
public sealed class KnowledgeAdminClient : IKnowledgeAdminClient
{
    private const string InternalSecretHeader = "X-Internal-Secret";
    private const int MaxAttempts = 3;

    private readonly HttpClient _httpClient;
    private readonly string _internalSecret;

    public KnowledgeAdminClient(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _internalSecret = configuration["KnowledgeService:InternalApiSecret"] ?? string.Empty;
    }

    public async Task<KnowledgeDocumentPage> ListDocumentsAsync(
        int page, int pageSize, string? status, Guid? classroomId, string? search, CancellationToken ct = default)
    {
        var url = $"api/internal/documents?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(status)) url += $"&status={Uri.EscapeDataString(status)}";
        if (classroomId.HasValue && classroomId.Value != Guid.Empty) url += $"&classroomId={classroomId.Value}";
        if (!string.IsNullOrWhiteSpace(search)) url += $"&search={Uri.EscapeDataString(search)}";

        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<KnowledgeDocumentPage>(ct);
        return payload ?? new KnowledgeDocumentPage(Array.Empty<KnowledgeDocumentItem>(), 0, page, pageSize);
    }

    public async Task<IReadOnlyList<KnowledgeDocumentItem>> GetStatusBatchAsync(
        IReadOnlyCollection<Guid> fileIds, CancellationToken ct = default)
    {
        if (fileIds.Count == 0)
        {
            return Array.Empty<KnowledgeDocumentItem>();
        }

        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "api/internal/documents/status-batch")
            {
                Content = JsonContent.Create(new { fileIds })
            },
            ct);

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<List<KnowledgeDocumentItem>>(ct);
        return payload ?? new List<KnowledgeDocumentItem>();
    }

    public async Task<KnowledgeDocumentDetail?> GetDocumentDetailAsync(Guid fileId, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"api/internal/documents/{fileId}/detail"), ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null; // 7أ
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<KnowledgeDocumentDetail>(ct);
    }

    public async Task<KnowledgeStatsResult> GetStatsAsync(Guid? classroomId, CancellationToken ct = default)
    {
        var url = "api/internal/knowledge/stats";
        if (classroomId.HasValue && classroomId.Value != Guid.Empty) url += $"?classroomId={classroomId.Value}";

        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<KnowledgeStatsResult>(ct);
        return payload ?? new KnowledgeStatsResult(classroomId, 0, new Dictionary<string, int>(), 0, 0, 0);
    }

    public async Task ReindexFileAsync(Guid fileId, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"api/internal/documents/{fileId}/reindex"), ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new NotFoundException("File not found in the knowledge base."); // 7أ
        }
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            throw new InvalidOperationException("The ingestion queue is full. Please try again shortly.");
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task<BulkReindexResult> ReindexClassroomAsync(Guid classroomId, bool failedOnly, CancellationToken ct = default)
    {
        var url = $"api/internal/documents/classrooms/{classroomId}/reindex?failedOnly={(failedOnly ? "true" : "false")}";
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, url), ct);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            // 7ج: a reindex is already in progress for the classroom.
            throw new InvalidOperationException(
                "A reindex is already in progress for this classroom. Wait for it to finish.");
        }
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            // 7ب: too many files; narrow the scope (e.g. failed-only).
            var detail = await SafeReadDetailAsync(response, ct);
            throw new ArgumentException(detail ?? "The reindex batch is too large. Narrow the scope to failed files.");
        }

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<BulkReindexResult>(ct);
        return payload ?? new BulkReindexResult(classroomId, 0, 0, 0);
    }

    private static async Task<string?> SafeReadDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetail>(ct);
            return problem?.Detail;
        }
        catch
        {
            return null;
        }
    }

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

                // Retry 5xx EXCEPT 503 (queue-full is a meaningful terminal signal we translate).
                if ((int)response.StatusCode >= 500
                    && response.StatusCode != HttpStatusCode.ServiceUnavailable
                    && attempt < MaxAttempts)
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

    private sealed record ProblemDetail(string? Detail);
}
