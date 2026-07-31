namespace ClassroomService.Domain.Entities;

/// <summary>
/// One student's answer to one question. Unique on (QuestionId, StudentId) — the database is the
/// arbiter, not an application-level "have they answered yet?" read, which two concurrent submits
/// both pass.
///
/// <see cref="IsCorrect"/> and <see cref="PointsAwarded"/> are SNAPSHOT here at submission rather
/// than derived on read. This is a grade record: it must not change because a question was later
/// edited, an option removed, or a quiz cancelled. Totals are summed from these rows, filtered by
/// the quiz's status.
/// </summary>
public sealed class QuizAnswer
{
    public Guid Id { get; set; }

    /// <summary>Denormalised from the question so per-quiz result queries need no join.</summary>
    public Guid QuizId { get; set; }

    public Guid QuestionId { get; set; }
    public Guid StudentId { get; set; }

    /// <summary>
    /// The student's display name, captured at submission from their token.
    ///
    /// Denormalised on purpose, the same way StreamQuestion.UserName is: ClassroomService holds no
    /// user names (its own member list leaves FullName blank) and has no client to
    /// UserManagementService, so resolving them on read would mean inventing a new cross-service
    /// dependency just to label a results table. Snapshotting also suits a grade record — the marks
    /// stay attributable to the name the student had when they sat the quiz.
    /// </summary>
    public string StudentName { get; set; } = string.Empty;

    public Guid SelectedOptionId { get; set; }

    public bool IsCorrect { get; set; }
    public int PointsAwarded { get; set; }
    public DateTime AnsweredAtUtc { get; set; }

    public QuizQuestion Question { get; set; } = null!;
}
