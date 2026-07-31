namespace ClassroomService.Application.DTOs.Quiz;

// --- teacher writes (draft only) --------------------------------------------

public record QuizDraftRequest(string Title, List<QuestionDraftRequest> Questions);

public record QuestionDraftRequest(
    string Text,
    int Points,
    int TimeLimitSeconds,
    List<OptionDraftRequest> Options);

public record OptionDraftRequest(string Text, bool IsCorrect);

public record SubmitAnswerRequest(Guid QuestionId, Guid OptionId);

// --- teacher reads -----------------------------------------------------------

/// <summary>
/// The teacher's view: everything, including which option is correct and how many people picked
/// each. Never returned to a student — see <see cref="QuizStudentDto"/>.
/// </summary>
public record QuizTeacherDto(
    Guid Id,
    Guid SessionId,
    string Title,
    string Status,
    int TotalPoints,
    int TotalSeconds,
    DateTime? ClosesAtUtc,
    DateTime ServerNowUtc,
    int RespondentCount,
    List<QuizQuestionTeacherDto> Questions);

public record QuizQuestionTeacherDto(
    Guid Id,
    int Order,
    string Text,
    int Points,
    int TimeLimitSeconds,
    List<QuizOptionTeacherDto> Options);

public record QuizOptionTeacherDto(Guid Id, int Order, string Text, bool IsCorrect, int SelectedCount);

// --- student reads -----------------------------------------------------------

/// <summary>
/// The student's view. Structurally incapable of carrying the answer key: the option type here has
/// no IsCorrect field at all, so leaking it would take a deliberate code change rather than an
/// oversight.
///
/// <paramref name="ServerNowUtc"/> travels with <paramref name="ClosesAtUtc"/> so the browser can
/// compute an offset once and count down against the SERVER's clock. Counting down from the
/// device's own clock shows the wrong time to anyone whose machine is skewed.
/// </summary>
public record QuizStudentDto(
    Guid Id,
    Guid SessionId,
    string Title,
    string Status,
    int TotalPoints,
    DateTime? ClosesAtUtc,
    DateTime ServerNowUtc,
    List<QuizQuestionStudentDto> Questions);

public record QuizQuestionStudentDto(
    Guid Id,
    int Order,
    string Text,
    int Points,
    int TimeLimitSeconds,
    /// <summary>What this student has picked so far, so a reload restores their selections.</summary>
    Guid? SelectedOptionId,
    List<QuizOptionStudentDto> Options);

public record QuizOptionStudentDto(Guid Id, int Order, string Text);

/// <summary>
/// Acknowledgement only. Deliberately does NOT say whether the answer was right: answers can be
/// changed until the quiz closes, so revealing correctness on submit would let a student walk every
/// option until the response said yes.
/// </summary>
public record SubmitAnswerResultDto(Guid QuestionId, Guid SelectedOptionId, DateTime AnsweredAtUtc);

// --- results -----------------------------------------------------------------

public record QuizResultsDto(
    Guid QuizId,
    string Status,
    int TotalPoints,
    /// <summary>False for a cancelled quiz, whose marks are preserved but not counted anywhere.</summary>
    bool CountsTowardsMarks,
    List<StudentQuizResultDto> Students);

public record StudentQuizResultDto(
    Guid StudentId,
    int Score,
    int TotalPoints,
    int AnsweredCount,
    int CorrectCount);

/// <summary>A student's own result. Correctness per question is only populated once the quiz is closed.</summary>
public record MyQuizResultDto(
    Guid QuizId,
    string Status,
    int Score,
    int TotalPoints,
    bool CountsTowardsMarks,
    List<MyAnswerDto> Answers);

public record MyAnswerDto(Guid QuestionId, Guid SelectedOptionId, bool? IsCorrect, int PointsAwarded);

/// <summary>The server-owned limits, delivered so the composer UI cannot offer a rejected value.</summary>
public record QuizLimitsDto(
    int MaxQuestionsPerQuiz,
    int MinAnswersPerQuestion,
    int MaxAnswersPerQuestion,
    int DefaultSecondsPerQuestion,
    int MaxQuizDurationSeconds);
