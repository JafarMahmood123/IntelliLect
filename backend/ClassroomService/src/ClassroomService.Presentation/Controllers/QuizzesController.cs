using System.Security.Claims;
using ClassroomService.Application.Abstractions;
using ClassroomService.Application.DTOs.Quiz;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClassroomService.Presentation.Controllers;

/// <summary>
/// In-session quizzes. Teacher routes compose, publish, close and cancel; student routes read the
/// answer-key-free view and submit answers.
///
/// The split between <c>GET {quizId}</c> (teacher) and <c>GET {quizId}/student-view</c> is the
/// whole security model of this feature — the two return different types, and only one of them can
/// express which option is correct. Do not "simplify" them into one endpoint with a flag.
/// </summary>
[Authorize]
[Route("api/classrooms/{classroomId:guid}")]
public sealed class QuizzesController : ApiBaseController
{
    private readonly IQuizService _quizService;

    public QuizzesController(IQuizService quizService) => _quizService = quizService;

    /// <summary>
    /// The caller's display name from their token. Stored alongside each answer so a results table
    /// can name people — ClassroomService holds no user names of its own.
    /// </summary>
    private string UserName => User.FindFirst(ClaimTypes.Name)?.Value ?? "Unknown";

    /// <summary>The composer's bounds, so it can never offer a value publish would reject.</summary>
    [HttpGet("quiz-limits")]
    public IActionResult GetLimits() => Ok(_quizService.GetLimits());

    // --- teacher ----------------------------------------------------------------

    [HttpPost("sessions/{sessionId:guid}/quizzes")]
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> CreateDraft(
        Guid classroomId, Guid sessionId, [FromBody] QuizDraftRequest request, CancellationToken ct)
        => Ok(await _quizService.CreateDraftAsync(classroomId, sessionId, UserId, request, ct));

    /// <summary>
    /// Draft a quiz, in Draft, for the teacher to review and edit before publishing.
    ///
    /// <c>wholeSession</c> chooses between a quick test on what was just explained and a quiz over
    /// the whole lesson. Both land in the same composer and publish the same way.
    /// </summary>
    [HttpPost("sessions/{sessionId:guid}/quizzes/generate")]
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> GenerateDraft(
        Guid classroomId, Guid sessionId, [FromBody] GenerateQuizRequest request, CancellationToken ct)
        => Ok(await _quizService.GenerateDraftAsync(
            classroomId, sessionId, UserId, request.QuestionCount, request.WholeSession, ct));

    /// <summary>
    /// One generated question for the composer to append. Returns content only — nothing is
    /// persisted, so a teacher can generate, dislike it, and move on without leaving a draft behind.
    /// </summary>
    [HttpPost("sessions/{sessionId:guid}/quizzes/generate-question")]
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> GenerateQuestion(
        Guid classroomId, Guid sessionId, [FromBody] GenerateQuestionRequest request, CancellationToken ct)
        => Ok(await _quizService.GenerateQuestionAsync(
            classroomId, sessionId, UserId, request.Avoid, ct));

    /// <summary>Answers for a question the teacher wrote themselves. Also unpersisted.</summary>
    [HttpPost("sessions/{sessionId:guid}/quizzes/generate-answers")]
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> GenerateAnswers(
        Guid classroomId, Guid sessionId, [FromBody] GenerateAnswersRequest request, CancellationToken ct)
        => Ok(await _quizService.GenerateAnswersAsync(
            classroomId, sessionId, UserId, request.QuestionText, ct));

    [HttpPut("quizzes/{quizId:guid}")]
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> UpdateDraft(
        Guid classroomId, Guid quizId, [FromBody] QuizDraftRequest request, CancellationToken ct)
        => Ok(await _quizService.UpdateDraftAsync(classroomId, quizId, UserId, request, ct));

    [HttpPost("quizzes/{quizId:guid}/publish")]
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> Publish(Guid classroomId, Guid quizId, CancellationToken ct)
        => Ok(await _quizService.PublishAsync(classroomId, quizId, UserId, ct));

    [HttpPost("quizzes/{quizId:guid}/close")]
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> Close(Guid classroomId, Guid quizId, CancellationToken ct)
        => Ok(await _quizService.CloseAsync(classroomId, quizId, UserId, ct));

    /// <summary>
    /// More time on a running quiz. An empty <c>studentIds</c> extends it for the whole class;
    /// naming students gives the time only to them.
    /// </summary>
    [HttpPost("quizzes/{quizId:guid}/extend")]
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> Extend(
        Guid classroomId, Guid quizId, [FromBody] ExtendQuizRequest request, CancellationToken ct)
        => Ok(await _quizService.ExtendAsync(classroomId, quizId, UserId, request, ct));

    /// <summary>Withdraws the quiz from marking. Answers are kept; they simply stop counting.</summary>
    [HttpPost("quizzes/{quizId:guid}/cancel")]
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> Cancel(Guid classroomId, Guid quizId, CancellationToken ct)
        => Ok(await _quizService.CancelAsync(classroomId, quizId, UserId, ct));

    [HttpGet("quizzes/{quizId:guid}")]
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> GetForTeacher(Guid classroomId, Guid quizId, CancellationToken ct)
        => Ok(await _quizService.GetForTeacherAsync(classroomId, quizId, UserId, ct));

    [HttpGet("quizzes/{quizId:guid}/results")]
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> GetResults(Guid classroomId, Guid quizId, CancellationToken ct)
        => Ok(await _quizService.GetResultsAsync(classroomId, quizId, UserId, ct));

    // --- student ----------------------------------------------------------------

    /// <summary>
    /// The session's open quiz, or 204. This is what a student who joins after publication uses —
    /// without it they would only ever learn of a quiz from a broadcast they were not there for.
    /// </summary>
    [HttpGet("sessions/{sessionId:guid}/quizzes/open")]
    public async Task<IActionResult> GetOpenForSession(Guid classroomId, Guid sessionId, CancellationToken ct)
    {
        var quiz = await _quizService.GetOpenForSessionAsync(classroomId, sessionId, UserId, ct);
        return quiz is null ? NoContent() : Ok(quiz);
    }

    [HttpGet("quizzes/{quizId:guid}/student-view")]
    public async Task<IActionResult> GetForStudent(Guid classroomId, Guid quizId, CancellationToken ct)
        => Ok(await _quizService.GetForStudentAsync(classroomId, quizId, UserId, ct));

    [HttpPost("quizzes/{quizId:guid}/answers")]
    public async Task<IActionResult> SubmitAnswer(
        Guid classroomId, Guid quizId, [FromBody] SubmitAnswerRequest request, CancellationToken ct)
        => Ok(await _quizService.SubmitAnswerAsync(classroomId, quizId, UserId, UserName, request, ct));

    /// <summary>
    /// The student declares they have finished, instead of waiting for the timer. Freezes their
    /// answers; the rest of the class carries on.
    /// </summary>
    [HttpPost("quizzes/{quizId:guid}/submit")]
    public async Task<IActionResult> SubmitQuiz(Guid classroomId, Guid quizId, CancellationToken ct)
        => Ok(await _quizService.SubmitQuizAsync(classroomId, quizId, UserId, UserName, ct));

    /// <summary>
    /// Teacher: the whole session's marks — who the students are, what they scored, and how the
    /// class handled each question. Available live and after the session has ended.
    /// </summary>
    [HttpGet("sessions/{sessionId:guid}/quiz-summary")]
    [Authorize(Roles = "Teacher")]
    public async Task<IActionResult> GetSessionSummary(Guid classroomId, Guid sessionId, CancellationToken ct)
        => Ok(await _quizService.GetSessionSummaryAsync(classroomId, sessionId, UserId, ct));

    /// <summary>Student: their own quizzes and marks for the session.</summary>
    [HttpGet("sessions/{sessionId:guid}/my-quiz-summary")]
    public async Task<IActionResult> GetMySessionSummary(Guid classroomId, Guid sessionId, CancellationToken ct)
        => Ok(await _quizService.GetMySessionSummaryAsync(classroomId, sessionId, UserId, ct));

    [HttpGet("quizzes/{quizId:guid}/my-result")]
    public async Task<IActionResult> GetMyResult(Guid classroomId, Guid quizId, CancellationToken ct)
        => Ok(await _quizService.GetMyResultAsync(classroomId, quizId, UserId, ct));
}
