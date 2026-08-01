namespace ClassroomService.Application.DTOs.Quiz;

/// <summary>
/// The teacher's session-wide view: every student's standing across every quiz in the session, plus
/// a per-question breakdown of how the class answered.
///
/// Session-scoped rather than per-quiz because that is the question a teacher actually asks — "how
/// did my class do today", not "how did they do on quiz three". Cancelled quizzes are excluded from
/// the totals but still listed, so a teacher who cancelled one can see that it happened.
/// </summary>
public record SessionQuizSummaryDto(
    Guid SessionId,
    int QuizCount,
    int CountedQuizCount,
    int TotalPointsAvailable,
    /// <summary>
    /// Everyone who took part in a quiz this session — answered one, or finished one without
    /// answering. A student who took part and scored nothing appears with a zero rather than
    /// vanishing, because "who did not engage" is exactly what a teacher is looking for here.
    ///
    /// Participation, NOT attendance: this service records no attendance, and the enrolment list
    /// would name people who never joined the session at all.
    /// </summary>
    List<StudentScoreDto> Students,
    List<QuestionBreakdownDto> Questions);

public record StudentScoreDto(
    Guid StudentId,
    string StudentName,
    int Score,
    int TotalPointsAvailable,
    int AnsweredCount,
    int CorrectCount,
    /// <summary>Score as a percentage of what was available, rounded — for at-a-glance ranking.</summary>
    int Percentage,
    /// <summary>
    /// Every choice this student made, across every published quiz in the session — so a teacher can
    /// go from "they scored 4/10" to "here is the option they picked on question 3".
    ///
    /// Carries ids only. The question and option TEXT already travel once in
    /// <see cref="SessionQuizSummaryDto.Questions"/>; repeating it per student would multiply the
    /// payload by the size of the class to say the same thing.
    /// </summary>
    List<StudentAnswerDto> Answers);

/// <summary>
/// One student's choice on one question. Includes answers to CANCELLED quizzes, which are excluded
/// from the score but not from the record — a teacher reviewing what was asked should still see what
/// was answered.
/// </summary>
public record StudentAnswerDto(
    Guid QuizId,
    Guid QuestionId,
    Guid SelectedOptionId,
    bool IsCorrect,
    int PointsAwarded,
    DateTime AnsweredAtUtc);

/// <summary>
/// One question, and how the class handled it. The option tallies are what tell a teacher WHY a
/// question went badly — a wrong option taking most of the votes usually means a misconception
/// worth addressing, not a class that failed.
/// </summary>
public record QuestionBreakdownDto(
    Guid QuestionId,
    Guid QuizId,
    string QuizTitle,
    string QuizStatus,
    bool CountsTowardsMarks,
    int Order,
    string Text,
    int Points,
    int AnsweredCount,
    int CorrectCount,
    List<OptionBreakdownDto> Options);

public record OptionBreakdownDto(Guid OptionId, string Text, bool IsCorrect, int SelectedCount);

// --- student ------------------------------------------------------------------

/// <summary>
/// A student's own standing for the session: one row per quiz plus the running total. Carries no
/// information about anyone else, and no correct answers for a quiz that is still open.
/// </summary>
public record MySessionQuizSummaryDto(
    Guid SessionId,
    int Score,
    int TotalPointsAvailable,
    int Percentage,
    List<MyQuizScoreDto> Quizzes);

public record MyQuizScoreDto(
    Guid QuizId,
    string Title,
    string Status,
    bool CountsTowardsMarks,
    /// <summary>Withheld (0) until the quiz closes — answers are changeable until then.</summary>
    int Score,
    int TotalPoints,
    int AnsweredCount,
    int QuestionCount,
    /// <summary>
    /// The question-by-question review: what was asked, what this student picked, and which option
    /// was actually right.
    ///
    /// EMPTY while the quiz is still open. That is the whole reason the correctness fields below can
    /// be plain booleans — a review is either withheld entirely or complete, so there is no state in
    /// which this list exists and lies about the answer key. Do not "improve" this by sending the
    /// questions early with correctness blanked; the type would then be one forgotten conditional
    /// away from handing a student the answers mid-quiz.
    /// </summary>
    List<MyQuestionReviewDto> Questions);

public record MyQuestionReviewDto(
    Guid QuestionId,
    int Order,
    string Text,
    int Points,
    /// <summary>What this student picked, or null if they never answered this one.</summary>
    Guid? SelectedOptionId,
    /// <summary>Null means UNANSWERED, not withheld — a withheld review has no questions at all.</summary>
    bool? IsCorrect,
    int PointsAwarded,
    List<MyOptionReviewDto> Options);

public record MyOptionReviewDto(Guid OptionId, int Order, string Text, bool IsCorrect);
