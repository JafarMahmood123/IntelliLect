using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Qa;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClassroomService.Presentation.Controllers;

/// <summary>
/// User-facing Q&amp;A over a classroom's material (F-5). Browser-callable and JWT-authenticated;
/// membership is enforced server-side and the retrieval scope is the route classroom id — never a
/// value from the client. Wraps KnowledgeService's grounded RAG answer without exposing its
/// internal secret.
/// </summary>
[Authorize]
[Route("api/classrooms/{classroomId:guid}/qa")]
public sealed class QaController : ApiBaseController
{
    private readonly IClassroomQaService _qaService;

    public QaController(IClassroomQaService qaService)
    {
        _qaService = qaService;
    }

    /// <summary>
    /// Answer a question about this classroom's material. 403 for non-members, 404 for unknown
    /// classroom, 422 for an empty question. Returns a grounded answer with citations, or
    /// hasAnswer=false when no relevant material is found.
    /// </summary>
    [HttpPost("answer")]
    public async Task<IActionResult> Answer(
        Guid classroomId, [FromBody] QaAnswerRequest request, CancellationToken ct)
    {
        var result = await _qaService.AnswerAsync(classroomId, UserId, request.Question, ct);
        return Ok(result);
    }
}
