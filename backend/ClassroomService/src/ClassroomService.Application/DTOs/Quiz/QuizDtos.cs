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

/// <summary>
/// More time on a running quiz.
///
/// <paramref name="StudentIds"/> empty means the whole class, which moves the quiz's own deadline
/// and stores nothing per student. Naming students instead gives the time only to them — the case
/// where a class-wide extension would be wrong, because it hands the extra minutes to everyone
/// including those who already finished.
/// </summary>
public record ExtendQuizRequest(int Seconds, List<Guid>? StudentIds = null);

/// <summary>
/// How many questions to ask the assistant for. Clamped server-side to the configured maximum,
/// so a client asking for more gets the most it is allowed rather than an error.
/// </summary>
public record GenerateQuizRequest(int QuestionCount = 3, bool WholeSession = false);

/// <summary>Questions already in the composer, so a newly generated one does not repeat them.</summary>
public record GenerateQuestionRequest(List<string>? Avoid = null);

/// <summary>The teacher's own question, for which the assistant writes the answers.</summary>
public record GenerateAnswersRequest(string QuestionText);

/// <summary>
/// A point where the classroom's material contradicted what the teacher said aloud.
///
/// The assistant writes the question so the option the MATERIAL supports is the correct one — a
/// class must never be marked wrong for having listened. This carries the disagreement back so the
/// teacher sees it before publishing: an answer key that silently contradicts what they told the
/// room a minute ago is worse than the slip it corrects.
///
/// NOT persisted. It describes this act of generation, not the quiz — once the teacher has read it
/// and accepted or edited the draft, it has done its job.
/// </summary>
public record QuizCorrectionDto(string Taught, string Corrected);

/// <summary>
/// One generated question, returned for the composer to append. NOT persisted: the teacher is
/// mid-compose, and writing a row per button press would litter the session with abandoned drafts.
/// Marks and timing are this service's defaults, so the composer can drop it straight in.
/// </summary>
public record GeneratedQuestionDraftDto(
    string Text,
    int Points,
    int TimeLimitSeconds,
    List<OptionDraftRequest> Options,
    List<QuizCorrectionDto> Corrections);

/// <summary>
/// A generated draft and what the assistant had to correct to write it.
///
/// A wrapper rather than fields on <see cref="QuizTeacherDto"/>, because corrections belong to the
/// GENERATION and not to the quiz: the same quiz read back a minute later is the same quiz, and
/// storing a transient note about how it came to be would put a model's opinion into an academic
/// record.
/// </summary>
public record GeneratedQuizDraftDto(QuizTeacherDto Quiz, List<QuizCorrectionDto> Corrections);

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
    /// <summary>How many students have declared themselves finished. The teacher can close once
    /// everyone has, instead of waiting out a timer nobody is still using.</summary>
    int SubmittedCount,
    /// <summary>
    /// Who is taking part, so the teacher can give time to a NAMED student rather than to the room.
    /// Empty for a draft and for a quiz nobody has touched.
    /// </summary>
    List<QuizRespondentDto> Respondents,
    List<QuizQuestionTeacherDto> Questions);

/// <summary>
/// One student's standing in a running quiz — enough to decide who needs longer, and nothing more.
/// Carries no answers: which options they picked is a marks question, and it lives in the session
/// summary where the teacher goes to review, not in the panel they run the quiz from.
/// </summary>
public record QuizRespondentDto(
    Guid StudentId,
    string StudentName,
    int AnsweredCount,
    bool HasSubmitted,
    /// <summary>Their own deadline. Later than the quiz's when they have been given extra time.</summary>
    DateTime? ClosesAtUtc,
    bool HasExtraTime);

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
    /// <summary>When this student declared themselves finished, or null if they have not. Once
    /// set, their answers are frozen and the client stops offering to change them.</summary>
    DateTime? SubmittedAtUtc,
    List<QuizQuestionStudentDto> Questions);

/// <summary>
/// Acknowledgement that a student has finished. Carries no marks: freezing one student's answers
/// does not close the quiz for the rest of the class, and telling an early finisher which options
/// were right would hand them the answer key while their classmates are still choosing.
/// </summary>
public record QuizSubmissionDto(
    Guid QuizId,
    DateTime SubmittedAtUtc,
    int AnsweredCount,
    int QuestionCount);

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
