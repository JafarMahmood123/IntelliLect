using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamingService.Application.Abstractions;
using StreamingService.Infrastructure.Configuration;

namespace StreamingService.Infrastructure.Services;

/// <summary>
/// Typed HttpClient over ClassroomService's <c>GET /api/internal/classrooms/{id}/access/{userId}</c>.
///
/// **Fails closed, and every path through it does.** A 404, a 500, a timeout, a connection refused,
/// a body that will not parse — all of them return <see cref="ClassroomAccess.None"/>, so an
/// unanswerable question refuses the join token. The alternative is the shape §7b already found on
/// the internal secret: a guard that admits everybody precisely when something is wrong.
///
/// The cost of that choice is stated rather than hidden: while ClassroomService is down, nobody new
/// can join a live lecture. People already in the room are unaffected — LiveKit holds their token
/// and never asks us again — so the failure is "no new joins" rather than "the class stops". That
/// is the right way round, and it is why the timeout here is short.
///
/// No retry. The caller is a person waiting on a page, the failure is reported to them, and pressing
/// the button again is a better retry than one they cannot see.
/// </summary>
public sealed class ClassroomInternalClient : IClassroomInternalClient
{
    private const string InternalSecretHeader = "X-Internal-Secret";

    private readonly HttpClient _httpClient;
    private readonly ClassroomServiceOptions _options;
    private readonly ILogger<ClassroomInternalClient> _logger;

    public ClassroomInternalClient(
        HttpClient httpClient,
        IOptions<ClassroomServiceOptions> options,
        ILogger<ClassroomInternalClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ClassroomAccess> GetAccessAsync(
        Guid classroomId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"api/internal/classrooms/{classroomId}/access/{userId}");

            if (!string.IsNullOrWhiteSpace(_options.InternalApiSecret))
            {
                request.Headers.TryAddWithoutValidation(InternalSecretHeader, _options.InternalApiSecret);
            }

            using var response = await _httpClient.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // The stream names a classroom that ClassroomService does not have. Not an outage —
                // a deleted classroom, or two databases that disagree. Either way: no.
                _logger.LogWarning(
                    "Classroom {ClassroomId} is unknown to ClassroomService; refusing access for user {UserId}.",
                    classroomId, userId);
                return ClassroomAccess.None;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Membership check for user {UserId} in classroom {ClassroomId} failed with status "
                    + "{StatusCode}; refusing access.",
                    userId, classroomId, (int)response.StatusCode);
                return ClassroomAccess.None;
            }

            var body = await response.Content.ReadFromJsonAsync<AccessResponse>(ct);
            if (body is null)
            {
                _logger.LogError(
                    "Membership check for user {UserId} in classroom {ClassroomId} returned a body that "
                    + "could not be read; refusing access.",
                    userId, classroomId);
                return ClassroomAccess.None;
            }

            return new ClassroomAccess(body.IsMember, body.IsTeacher);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller hung up. Not our failure to log as one — §11.7's 499 lesson.
            throw;
        }
        catch (Exception failure)
        {
            _logger.LogError(
                failure,
                "Membership check for user {UserId} in classroom {ClassroomId} could not be completed; "
                + "refusing access.",
                userId, classroomId);
            return ClassroomAccess.None;
        }
    }

    /// <summary>Matches ClassroomService's <c>ClassroomAccessResult</c>.</summary>
    private sealed record AccessResponse(
        [property: JsonPropertyName("isMember")] bool IsMember,
        [property: JsonPropertyName("isTeacher")] bool IsTeacher);
}
