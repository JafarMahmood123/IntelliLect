using ClassroomService.Application.Abstractions;
using ClassroomService.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClassroomService.Presentation.Controllers;

/// <summary>
/// Recording listing &amp; status for a classroom (R-2). Metadata only — no download URL yet (R-3).
/// Access is gated by classroom membership inside the service (403 for non-members).
/// </summary>
[Authorize]
[Route("api/classrooms/{classroomId:guid}/recordings")]
public sealed class RecordingsController : ApiBaseController
{
    private readonly IClassroomRecordingService _recordingService;

    public RecordingsController(IClassroomRecordingService recordingService)
    {
        _recordingService = recordingService;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        Guid classroomId,
        [FromQuery] Guid? sessionId,
        [FromQuery] RecordingStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken ct = default)
    {
        var validPage = page < 1 ? 1 : page;
        var validPageSize = pageSize is < 1 or > 100 ? 10 : pageSize;

        var result = await _recordingService.ListRecordingsAsync(
            classroomId, UserId, sessionId, status, validPage, validPageSize, ct);

        return Ok(result);
    }

    [HttpGet("{recordingId:guid}")]
    public async Task<IActionResult> Get(Guid classroomId, Guid recordingId, CancellationToken ct)
    {
        var result = await _recordingService.GetRecordingAsync(classroomId, recordingId, UserId, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns a short-lived, GET-only pre-signed URL to download the recording's MP4 directly
    /// from S3 (R-3). 403 for non-members, 404 for unknown/cross-classroom, 409 if not Available.
    /// </summary>
    [HttpGet("{recordingId:guid}/download-url")]
    public async Task<IActionResult> GetDownloadUrl(Guid classroomId, Guid recordingId, CancellationToken ct)
    {
        var result = await _recordingService.GetDownloadUrlAsync(classroomId, recordingId, UserId, ct);
        return Ok(result);
    }
}
