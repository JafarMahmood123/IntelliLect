using ClassroomService.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClassroomService.Presentation.Controllers;

/// <summary>
/// Session-summary listing, status &amp; secure download for a classroom (S-4). Metadata only — the
/// bytes are downloaded directly from S3 via a short-lived pre-signed URL. Access is gated by
/// classroom membership inside the service (403 for non-members). Mirrors <see cref="RecordingsController"/>.
/// </summary>
[Authorize]
[Route("api/classrooms/{classroomId:guid}/summaries")]
public sealed class SummariesController : ApiBaseController
{
    private readonly IClassroomSummaryService _summaryService;

    public SummariesController(IClassroomSummaryService summaryService)
    {
        _summaryService = summaryService;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        Guid classroomId,
        [FromQuery] Guid? sessionId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken ct = default)
    {
        var validPage = page < 1 ? 1 : page;
        var validPageSize = pageSize is < 1 or > 100 ? 10 : pageSize;

        var result = await _summaryService.ListSummariesAsync(
            classroomId, UserId, sessionId, validPage, validPageSize, ct);

        return Ok(result);
    }

    [HttpGet("{summaryId:guid}")]
    public async Task<IActionResult> Get(Guid classroomId, Guid summaryId, CancellationToken ct)
    {
        var result = await _summaryService.GetSummaryAsync(classroomId, summaryId, UserId, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns a short-lived, GET-only pre-signed URL to download the summary's PDF or Markdown
    /// directly from S3 (<c>?format=pdf|md</c>, default pdf). 403 for non-members, 404 for
    /// unknown/cross-classroom, 409 if not Available.
    /// </summary>
    [HttpGet("{summaryId:guid}/download-url")]
    public async Task<IActionResult> GetDownloadUrl(
        Guid classroomId,
        Guid summaryId,
        [FromQuery] string? format,
        CancellationToken ct)
    {
        var result = await _summaryService.GetDownloadUrlAsync(classroomId, summaryId, UserId, format, ct);
        return Ok(result);
    }
}
