using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using StreamingService.Application.Abstractions;
using StreamingService.Application.DTOs;
using StreamingService.Application.DTOs.Question;

namespace StreamingService.Presentation.Controllers;

[Authorize]
[Route("api/streams")]
public sealed class StreamsController : ApiBaseController
{
    private readonly IStreamService _streamService;
    private readonly IInteractionService _interactionService;
    private readonly ILogger<StreamsController> _logger;

    public StreamsController(
        IStreamService streamService,
        IInteractionService interactionService,
        ILogger<StreamsController> logger)
    {
        _streamService = streamService;
        _interactionService = interactionService;
        _logger = logger;
    }

    [HttpGet("{sessionId:guid}")]
    public async Task<IActionResult> GetStream(Guid sessionId, CancellationToken ct)
    {
        var role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "Student";
        var userName = User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "Anonymous";
        var response = await _streamService.GetStreamBySessionIdAsync(sessionId, UserId, role, userName, ct);
        return Ok(response);
    }

    [HttpPost("{sessionId:guid}/join")]
    public async Task<IActionResult> Join(Guid sessionId, CancellationToken ct)
    {
        _logger.LogDebug("Join stream requested. SessionId: {SessionId}, UserId: {UserId}", sessionId, UserId);
        await _streamService.JoinStreamAsync(sessionId, UserId, ct);
        return NoContent();
    }

    [HttpDelete("{sessionId:guid}/leave")]
    public async Task<IActionResult> Leave(Guid sessionId, CancellationToken ct)
    {
        _logger.LogDebug("Leave stream requested. SessionId: {SessionId}, UserId: {UserId}", sessionId, UserId);
        await _streamService.LeaveStreamAsync(sessionId, UserId, ct);
        return NoContent();
    }

    [HttpPut("{sessionId:guid}/hand-raise")]
    public async Task<IActionResult> ToggleHandRaise(Guid sessionId, [FromQuery] bool isRaised, CancellationToken ct)
    {
        _logger.LogDebug(
            "Hand raise toggle requested. SessionId: {SessionId}, UserId: {UserId}, IsRaised: {IsRaised}",
            sessionId, UserId, isRaised);
        await _interactionService.ToggleHandRaiseAsync(sessionId, UserId, isRaised, ct);
        return Ok(new { Message = isRaised ? "Hand raised." : "Hand lowered." });
    }

    [HttpPut("{sessionId:guid}/publish-policy")]
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> UpdatePublishPolicy(
        Guid sessionId,
        [FromBody] UpdatePublishPolicyRequest request,
        CancellationToken ct)
    {
        _logger.LogDebug(
            "Publish policy update requested. SessionId: {SessionId}, UserId: {UserId}, Audio: {Audio}, Video: {Video}",
            sessionId, UserId, request.CanPublishAudio, request.CanPublishVideo);
        var policy = await _streamService.UpdateStudentPublishPolicyAsync(
            sessionId, UserId, request.CanPublishAudio, request.CanPublishVideo, ct);
        return Ok(policy);
    }

    [HttpGet("{sessionId:guid}/chat")]
    public async Task<IActionResult> GetChatHistory(
        Guid sessionId,
        CancellationToken ct,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var result = await _interactionService.GetChatHistoryPagedAsync(sessionId, page, pageSize, ct);
        return Ok(result);
    }

    [HttpGet("{sessionId:guid}/questions")]
    public async Task<IActionResult> GetQuestions(
        Guid sessionId,
        CancellationToken ct,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var result = await _interactionService.GetQuestionsPagedAsync(sessionId, page, pageSize, ct);
        return Ok(result);
    }

    [HttpPost("{sessionId:guid}/questions")]
    public async Task<IActionResult> AskQuestion(
        Guid sessionId,
        [FromBody] AskQuestionRequest request,
        CancellationToken ct)
    {
        _logger.LogDebug("Ask question requested. SessionId: {SessionId}, UserId: {UserId}", sessionId, UserId);
        var userName = User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "Anonymous";
        await _interactionService.AskQuestionAsync(sessionId, UserId, userName, request.QuestionText, ct);
        return Ok(new { Message = "Question submitted successfully." });
    }

    [HttpPost("{sessionId:guid}/questions/{questionId:guid}/answer")]
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> AnswerQuestion(
        Guid sessionId,
        Guid questionId,
        [FromBody] AnswerQuestionRequest request,
        CancellationToken ct)
    {
        _logger.LogDebug(
            "Answer question requested. SessionId: {SessionId}, QuestionId: {QuestionId}, UserId: {UserId}",
            sessionId, questionId, UserId);
        await _interactionService.AnswerQuestionAsync(questionId, UserId, request.AnswerText, ct);
        return Ok(new { Message = "Question answered successfully." });
    }
}
