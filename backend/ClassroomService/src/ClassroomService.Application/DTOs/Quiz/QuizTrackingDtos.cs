namespace ClassroomService.Application.DTOs.Quiz;

/// <summary>
/// The teacher's view of a whole CLASSROOM, across every session it has run.
///
/// The session summary answers "how did today go". This answers the other question a teacher has —
/// "how is this class doing" — which no amount of reading one session at a time gives you, because
/// the interesting facts are the cumulative ones: who is falling behind over weeks, which lesson
/// went worst, who stopped turning up.
/// </summary>
public record ClassroomQuizTrackingDto(
    Guid ClassroomId,
    /// <summary>Enrolled, from the member list — not everyone here has necessarily sat a quiz.</summary>
    int EnrolledStudentCount,
    /// <summary>How many students have actually taken part in at least one quiz.</summary>
    int ActiveStudentCount,
    int SessionCount,
    /// <summary>Sessions that actually ran a quiz. The rest have nothing to track.</summary>
    int SessionsWithQuizzesCount,
    int QuizCount,
    int TotalPointsAvailable,
    /// <summary>Mean of the participating students' percentages, rounded.</summary>
    int ClassAveragePercentage,
    List<StudentTrackingDto> Students,
    List<SessionTrackingDto> Sessions);

/// <summary>
/// One student's standing across the whole classroom.
///
/// <paramref name="TotalPointsAvailable"/> is what was available to EVERYONE, not what this student
/// happened to be present for. A percentage measured against only the quizzes they turned up to
/// would flatter the student who missed the hard week, and the gap between the two numbers is
/// exactly what a teacher is looking for.
/// </summary>
public record StudentTrackingDto(
    Guid StudentId,
    string StudentName,
    int QuizzesTaken,
    int QuizCount,
    int AnsweredCount,
    int CorrectCount,
    int Score,
    int TotalPointsAvailable,
    int Percentage,
    int SessionsTakenPart,
    /// <summary>Sessions that ran a quiz, so "3 of 8" reads as attendance-ish without pretending
    /// to be attendance. See <see cref="ClassroomQuizTrackingDto"/> for why they differ.</summary>
    int SessionsWithQuizzesCount);

/// <summary>One lesson's headline numbers, for spotting the session that went badly.</summary>
public record SessionTrackingDto(
    Guid SessionId,
    string Title,
    DateTime ScheduledAtUtc,
    DateTime? StartedAtUtc,
    int QuizCount,
    int TotalPoints,
    int ParticipantCount,
    int AveragePercentage);

// --- student ------------------------------------------------------------------

/// <summary>
/// A student's own progress across the classroom. Names nobody else and carries no answers — only
/// their own totals, and the class average so a number has something to mean.
/// </summary>
public record MyClassroomQuizTrackingDto(
    Guid ClassroomId,
    int Score,
    int TotalPointsAvailable,
    int Percentage,
    int QuizzesTaken,
    int QuizCount,
    int SessionsTakenPart,
    int SessionsWithQuizzesCount,
    /// <summary>What the class averaged. Anonymous by construction — it is one number.</summary>
    int ClassAveragePercentage,
    List<MySessionTrackingDto> Sessions);

public record MySessionTrackingDto(
    Guid SessionId,
    string Title,
    DateTime ScheduledAtUtc,
    DateTime? StartedAtUtc,
    int Score,
    int TotalPoints,
    int Percentage,
    int QuizzesTaken,
    int QuizCount);
