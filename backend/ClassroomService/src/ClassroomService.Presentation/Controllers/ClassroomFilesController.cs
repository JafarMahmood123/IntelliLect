using ClassroomService.Application.Abstractions;
using ClassroomService.Presentation.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ClassroomService.Presentation.Controllers;

[Authorize]
[Route("api/classrooms/{classroomId:guid}/files")]
public sealed class ClassroomFilesController : ApiBaseController
{
    private readonly IClassroomFileService _fileService;

    public ClassroomFilesController(IClassroomFileService fileService)
    {
        _fileService = fileService;
    }

    /// <summary>
    /// The upload control's bounds, so it can never offer a file the server would reject. Readable
    /// by any member — the limits are not a secret, and the control needs them before a teacher
    /// picks a file.
    /// </summary>
    [HttpGet("upload-limits")]
    public IActionResult GetUploadLimits() => Ok(_fileService.GetUploadLimits());

    /// <summary>
    /// Stores a classroom material file. The size ceiling is applied by
    /// <see cref="UploadSizeLimitFilter"/> before the body is read; the exact per-file size and
    /// format rules are enforced by the service.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Teacher")]
    [ServiceFilter(typeof(UploadSizeLimitFilter))]
    public async Task<IActionResult> Upload(Guid classroomId, IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0) return BadRequest("No file uploaded.");

        using var stream = file.OpenReadStream();
        var response = await _fileService.UploadFileAsync(
            classroomId,
            UserId,
            stream,
            file.FileName,
            file.ContentType,
            ct);

        return Ok(response);
    }

    [HttpGet]
    public async Task<IActionResult> GetFiles(Guid classroomId, CancellationToken ct)
    {
        var response = await _fileService.GetClassroomFilesAsync(classroomId, UserId, ct);
        return Ok(response);
    }

    [HttpDelete("{fileId:guid}")]
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> DeleteFile(Guid fileId, CancellationToken ct)
    {
        await _fileService.DeleteFileAsync(fileId, UserId, ct);
        return NoContent();
    }

    /// <summary>
    /// Returns a classroom file's RAG indexing status (Pending/Processing/Done/Failed) for a
    /// member. 403 for non-members, 404 for unknown/cross-classroom files. The status is read
    /// from RagService server-side — the internal secret never reaches the browser.
    /// </summary>
    [HttpGet("{fileId:guid}/indexing-status")]
    public async Task<IActionResult> GetIndexingStatus(Guid classroomId, Guid fileId, CancellationToken ct)
    {
        var result = await _fileService.GetFileIndexingStatusAsync(classroomId, fileId, UserId, ct);
        return Ok(result);
    }

    /// <summary>
    /// Streams a classroom material file to the caller through the API/gateway (as an attachment),
    /// so the browser stays on the app origin instead of hitting the raw MinIO port. 403 for
    /// non-members, 404 for unknown/cross-classroom files.
    /// </summary>
    [HttpGet("{fileId:guid}/download")]
    public async Task<IActionResult> Download(Guid classroomId, Guid fileId, CancellationToken ct)
    {
        var result = await _fileService.GetFileDownloadAsync(classroomId, fileId, UserId, ct);
        return File(result.Content, result.ContentType, result.FileName);
    }
}