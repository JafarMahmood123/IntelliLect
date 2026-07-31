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
    int Percentage);

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
    int QuestionCount);
