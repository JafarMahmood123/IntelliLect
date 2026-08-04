using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Summary;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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
    private readonly IFileStorageService _fileStorage;

    public SummariesController(IClassroomSummaryService summaryService, IFileStorageService fileStorage)
    {
        _summaryService = summaryService;
        _fileStorage = fileStorage;
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
    /// The classroom's teacher re-requests a summary that ended up Failed. Returns the summary
    /// back in <c>Generating</c>; the actual work happens asynchronously in RagService.
    /// </summary>
    /// <remarks>
    /// Teacher-only, and ownership is checked inside the service — the read endpoints on this
    /// controller are gated on membership, which would let a student spend an LLM run.
    /// 409 when the summary is not Failed, so a double-click or a click on an Available summary
    /// is refused rather than destroying a good summary (the S3 keys are deterministic).
    /// </remarks>
    [HttpPost("{summaryId:guid}/regenerate")]
    [Authorize(Roles = "Teacher")]
    [ProducesResponseType(typeof(SummarySummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Regenerate(Guid classroomId, Guid summaryId, CancellationToken ct)
    {
        var result = await _summaryService.RegenerateSummaryAsync(classroomId, summaryId, UserId, ct);
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

    /// <summary>
    /// Streams the summary's PDF or Markdown (<c>?format=pdf|md</c>, default pdf) through the
    /// API/gateway as an attachment, so the browser stays on the app origin instead of hitting the
    /// raw MinIO port. Same checks: 403 non-member, 404 unknown/cross-classroom, 409 if not Available.
    /// </summary>
    [HttpGet("{summaryId:guid}/download")]
    public async Task<IActionResult> Download(
        Guid classroomId,
        Guid summaryId,
        [FromQuery] string? format,
        CancellationToken ct)
    {
        var target = await _summaryService.GetDownloadTargetAsync(classroomId, summaryId, UserId, format, ct);
        var stream = await _fileStorage.OpenReadAsync(target.S3Key, ct);
        return File(stream, target.ContentType, target.FileName);
    }
}
