using UserManagementService.Application.Abstractions;
using UserManagementService.Application.Common;
using UserManagementService.Application.DTOs.Session;
using UserManagementService.Domain.Entities;

namespace UserManagementService.Application.SessionMonitoring;

public sealed class SessionMonitorService : ISessionMonitorService
{
    private const string LiveStatus = "Live";
    private const int LiveFetchPageSize = 100;

    private readonly IClassroomInternalClient _classroomClient;
    private readonly IStreamingInternalClient _streamingClient;
    private readonly ILiveAssistantInternalClient _assistantClient;
    private readonly IUserRepository _userRepository;

    public SessionMonitorService(
        IClassroomInternalClient classroomClient,
        IStreamingInternalClient streamingClient,
        ILiveAssistantInternalClient assistantClient,
        IUserRepository userRepository)
    {
        _classroomClient = classroomClient;
        _streamingClient = streamingClient;
        _assistantClient = assistantClient;
        _userRepository = userRepository;
    }

    // Step 3: the paged session list, enriched with each session's teacher.
    public async Task<PagedResult<SessionMonitorItem>> GetSessionsAsync(SearchSessionsRequest request, CancellationToken ct = default)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = Math.Clamp(request.PageSize < 1 ? 20 : request.PageSize, 1, 100);

        var result = await _classroomClient.GetSessionsAsync(page, pageSize, request.Search, request.Status, request.ClassroomId, ct);
        var teacherById = await LoadTeachersAsync(result.Items.Select(s => s.TeacherId), ct);

        var items = result.Items.Select(s =>
        {
            teacherById.TryGetValue(s.TeacherId, out var teacher);
            return new SessionMonitorItem(
                s.SessionId, s.ClassroomId, s.ClassName, s.TeacherId,
                FullName(teacher), teacher?.Email,
                s.Title, s.Status, s.ScheduledAtUtc, s.StartedAtUtc, s.EndedAtUtc,
                s.RecordingStatus, s.SummaryStatus);
        }).ToList();

        return new PagedResult<SessionMonitorItem>(items, result.TotalCount, page, pageSize);
    }

    // Step 4: live sessions plus their real-time overlay (participants / recording / assistant).
    public async Task<LiveSessionsResponse> GetLiveSessionsAsync(CancellationToken ct = default)
    {
        // Stored data first — this is what the view falls back to.
        var live = await _classroomClient.GetSessionsAsync(1, LiveFetchPageSize, null, LiveStatus, null, ct);
        var teacherById = await LoadTeachersAsync(live.Items.Select(s => s.TeacherId), ct);

        // Alternate path 4أ: the real-time sources are best-effort. If either is unreachable we
        // still return the sessions, with the live fields null and a flag for the client.
        IReadOnlyList<LiveStreamSnapshot> snapshots = Array.Empty<LiveStreamSnapshot>();
        IReadOnlyCollection<Guid> assistantSessions = Array.Empty<Guid>();
        var realtimeUnavailable = false;

        try
        {
            snapshots = await _streamingClient.GetLiveStreamsAsync(ct);
        }
        catch (Exception failure) when (DownstreamFailure.ShouldDegrade(failure, ct))
        {
            realtimeUnavailable = true;
        }

        try
        {
            assistantSessions = await _assistantClient.GetActiveSessionIdsAsync(ct);
        }
        catch (Exception failure) when (DownstreamFailure.ShouldDegrade(failure, ct))
        {
            realtimeUnavailable = true;
        }

        var snapshotBySession = snapshots.ToDictionary(s => s.SessionId);
        var assistantSet = assistantSessions.ToHashSet();

        var items = live.Items.Select(s =>
        {
            teacherById.TryGetValue(s.TeacherId, out var teacher);
            snapshotBySession.TryGetValue(s.SessionId, out var snapshot);

            return new LiveSessionItem(
                s.SessionId, s.ClassroomId, s.ClassName, s.TeacherId, FullName(teacher),
                s.Title, s.StartedAtUtc,
                ParticipantCount: snapshot?.ParticipantCount,
                IsRecording: snapshot?.IsRecording,
                AssistantRunning: realtimeUnavailable ? null : assistantSet.Contains(s.SessionId));
        }).ToList();

        return new LiveSessionsResponse(items, realtimeUnavailable);
    }

    // Steps 5-8: validate the reason, then delegate to ClassroomService, which owns the session
    // status and runs the end path (stream end + summary trigger), best-effort per step.
    public async Task<ForceEndSessionResult> ForceEndAsync(Guid sessionId, string reason, CancellationToken ct = default)
    {
        // Alternate path 5أ: a reason is mandatory.
        var trimmedReason = (reason ?? string.Empty).Trim();
        if (trimmedReason.Length == 0)
        {
            throw new ArgumentException("A reason for force-ending the session is required.");
        }

        // NotFoundException (6أ) propagates from the client.
        var result = await _classroomClient.ForceEndSessionAsync(sessionId, trimmedReason, ct);

        return new ForceEndSessionResult(
            result.SessionId, result.Status, result.AlreadyEnded, result.StreamEnded, result.SummaryTriggered);
    }

    // Step 3: read-only impact preview. Returns null (-> 404) if the session does not exist (5أ).
    public async Task<SessionDeletionImpactResult?> GetDeletionImpactAsync(Guid sessionId, CancellationToken ct = default)
    {
        var impact = await _classroomClient.GetSessionDeletionImpactAsync(sessionId, ct);
        if (impact is null)
        {
            return null;
        }

        return new SessionDeletionImpactResult(
            impact.SessionId,
            impact.Title,
            impact.Status,
            impact.HasRecording,
            impact.HasSummary,
            impact.HasTranscript,
            impact.StorageBytes,
            impact.IsLive,
            impact.TranscriptUnavailable);
    }

    // Steps 5-8: validate the reason (4أ), then delegate to ClassroomService, which owns the session
    // and purges its recording/summary/transcript.
    public async Task<SessionDeletionSummary> DeleteSessionAsync(Guid sessionId, string reason, CancellationToken ct = default)
    {
        var trimmedReason = (reason ?? string.Empty).Trim();
        if (trimmedReason.Length == 0)
        {
            throw new ArgumentException("A deletion reason is required.");
        }

        // The client maps ClassroomService's 404 -> NotFoundException (5أ) and 409 ->
        // InvalidOperationException (5ب, live session).
        var result = await _classroomClient.DeleteSessionAsync(sessionId, trimmedReason, ct);

        return new SessionDeletionSummary(
            result.SessionId, result.RecordingDeleted, result.SummaryDeleted, result.TranscriptDeleted);
    }

    private async Task<Dictionary<Guid, User>> LoadTeachersAsync(IEnumerable<Guid> teacherIds, CancellationToken ct)
    {
        var ids = teacherIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, User>();
        }

        var teachers = await _userRepository.GetByIdsAsync(ids, ct);
        return teachers.ToDictionary(u => u.Id);
    }

    private static string? FullName(User? user) => user is null ? null : $"{user.FirstName} {user.LastName}";
}
