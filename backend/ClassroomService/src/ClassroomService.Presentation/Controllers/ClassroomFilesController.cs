using ClassroomService.Application.Abstractions;
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

    [HttpPost]
    [Authorize(Roles = "Teacher")]
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
        var response = await _fileService.GetClassroomFilesAsync(classroomId, ct);
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
    /// from KnowledgeService server-side — the internal secret never reaches the browser.
    /// </summary>
    [HttpGet("{fileId:guid}/indexing-status")]
    public async Task<IActionResult> GetIndexingStatus(Guid classroomId, Guid fileId, CancellationToken ct)
    {
        var result = await _fileService.GetFileIndexingStatusAsync(classroomId, fileId, UserId, ct);
        return Ok(result);
    }
}