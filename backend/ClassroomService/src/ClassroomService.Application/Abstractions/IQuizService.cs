using ClassroomService.Application.DTOs.Quiz;

namespace ClassroomService.Application.Abstractions;

/// <summary>
/// In-session quizzes: compose in Draft, publish to the room, auto-grade answers, close or cancel.
///
/// Teacher actions require the classroom's own teacher; student actions require membership. Both
/// resolve a quiz that belongs to the named classroom or 404 — never leaking across classrooms.
/// </summary>
public interface IQuizService
{
    Task<QuizTeacherDto> CreateDraftAsync(
        Guid classroomId, Guid sessionId, Guid teacherId, QuizDraftRequest request, CancellationToken ct = default);

    /// <summary>
    /// Builds a DRAFT from what the teacher has been explaining, using the assistant's record of
    /// the session and the classroom's material. The result is an ordinary draft — reviewed,
    /// edited and published exactly like a hand-composed one — so the teacher, not the model,
    /// decides what the class is asked.
    /// </summary>
    /// <param name="wholeSession">
    /// False for a quick test on the idea just finished. True for a quiz over the whole lesson,
    /// which the assistant reads from the session transcript instead of its recent ideas — the
    /// difference between "did you follow that?" and an end-of-lesson test.
    /// </param>
    Task<GeneratedQuizDraftDto> GenerateDraftAsync(
        Guid classroomId, Guid sessionId, Guid teacherId, int questionCount,
        bool wholeSession = false, CancellationToken ct = default);

    /// <summary>
    /// One generated question for the composer to append, from the same idea and material.
    /// <paramref name="avoid"/> carries the questions already written so it does not repeat them.
    /// Returns content only — nothing is persisted, because the teacher is still composing.
    /// </summary>
    Task<GeneratedQuestionDraftDto> GenerateQuestionAsync(
        Guid classroomId, Guid sessionId, Guid teacherId, IReadOnlyList<string>? avoid,
        CancellationToken ct = default);

    /// <summary>
    /// Answers for a question the TEACHER wrote, from that question plus the explanation they just
    /// gave. Also returns content only.
    /// </summary>
    Task<GeneratedQuestionDraftDto> GenerateAnswersAsync(
        Guid classroomId, Guid sessionId, Guid teacherId, string questionText,
        CancellationToken ct = default);

    /// <summary>
    /// The student declares they have finished, rather than sitting out the rest of the timer.
    /// Freezes THEIR answers only; everyone else keeps answering. Idempotent, so a double-click
    /// does not report a failure for something that already worked.
    /// </summary>
    Task<QuizSubmissionDto> SubmitQuizAsync(
        Guid classroomId, Guid quizId, Guid studentId, string studentName,
        CancellationToken ct = default);

    /// <summary>Replaces a DRAFT's content. Rejected once published — publishing freezes the quiz.</summary>
    Task<QuizTeacherDto> UpdateDraftAsync(
        Guid classroomId, Guid quizId, Guid teacherId, QuizDraftRequest request, CancellationToken ct = default);

    /// <summary>Draft -> Open. Validates against the configured limits and stamps the deadline.</summary>
    Task<QuizTeacherDto> PublishAsync(Guid classroomId, Guid quizId, Guid teacherId, CancellationToken ct = default);

    /// <summary>Open -> Closed, early. The deadline sweep closes it otherwise.</summary>
    Task<QuizTeacherDto> CloseAsync(Guid classroomId, Guid quizId, Guid teacherId, CancellationToken ct = default);

    /// <summary>
    /// More time on a running quiz: the whole class, or only the students named in the request.
    ///
    /// Rejected once the quiz has closed — extending then would reopen a quiz whose marks and
    /// answer key the class has already seen.
    /// </summary>
    Task<QuizTeacherDto> ExtendAsync(
        Guid classroomId, Guid quizId, Guid teacherId, ExtendQuizRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// -> Cancelled, from any state. Answers are PRESERVED and simply stop counting; cancelling
    /// must not destroy work already submitted.
    /// </summary>
    Task<QuizTeacherDto> CancelAsync(Guid classroomId, Guid quizId, Guid teacherId, CancellationToken ct = default);

    Task<QuizTeacherDto> GetForTeacherAsync(Guid classroomId, Guid quizId, Guid teacherId, CancellationToken ct = default);

    Task<QuizStudentDto> GetForStudentAsync(Guid classroomId, Guid quizId, Guid userId, CancellationToken ct = default);

    /// <summary>The session's open quiz, for someone joining after it was published. Null if none.</summary>
    Task<QuizStudentDto?> GetOpenForSessionAsync(
        Guid classroomId, Guid sessionId, Guid userId, CancellationToken ct = default);

    /// <summary>Records and grades one answer. Idempotent per (question, student) — a resubmit updates.</summary>
    Task<SubmitAnswerResultDto> SubmitAnswerAsync(
        Guid classroomId, Guid quizId, Guid studentId, string studentName,
        SubmitAnswerRequest request, CancellationToken ct = default);

    Task<QuizResultsDto> GetResultsAsync(Guid classroomId, Guid quizId, Guid teacherId, CancellationToken ct = default);

    Task<MyQuizResultDto> GetMyResultAsync(Guid classroomId, Guid quizId, Guid studentId, CancellationToken ct = default);

    /// <summary>
    /// Teacher: every student's standing across the whole session, plus a per-question breakdown.
    /// Works during the session and after it ends — nothing here depends on the session being live.
    /// </summary>
    Task<SessionQuizSummaryDto> GetSessionSummaryAsync(
        Guid classroomId, Guid sessionId, Guid teacherId, CancellationToken ct = default);

    /// <summary>Student: their own quizzes and marks for the session. Reveals nothing about others.</summary>
    Task<MySessionQuizSummaryDto> GetMySessionSummaryAsync(
        Guid classroomId, Guid sessionId, Guid studentId, CancellationToken ct = default);

    /// <summary>
    /// Teacher: the whole CLASSROOM across every session — cumulative scores per student, and how
    /// each lesson went. The question the session summary cannot answer, because the interesting
    /// facts over a term are the cumulative ones.
    /// </summary>
    Task<ClassroomQuizTrackingDto> GetClassroomTrackingAsync(
        Guid classroomId, Guid teacherId, CancellationToken ct = default);

    /// <summary>Student: their own progress across the classroom, plus the class average.</summary>
    Task<MyClassroomQuizTrackingDto> GetMyClassroomTrackingAsync(
        Guid classroomId, Guid studentId, CancellationToken ct = default);

    QuizLimitsDto GetLimits();
}
