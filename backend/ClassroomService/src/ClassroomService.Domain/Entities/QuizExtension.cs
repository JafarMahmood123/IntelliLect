namespace ClassroomService.Domain.Entities;

/// <summary>
/// Extra time granted to ONE student on one quiz.
///
/// Extending the whole class needs no row — the teacher moves <see cref="Quiz.ClosesAtUtc"/> and
/// everyone is covered. This exists for the other case: the student who joined late, dropped out
/// mid-quiz, or asked for longer, where moving the class deadline would hand the extra minutes to
/// everyone including those who already finished.
///
/// Stored as an ABSOLUTE deadline rather than "+N seconds", so re-reading it never depends on when
/// it is read and two grants in a row cannot compound into a surprise. Unique on
/// (QuizId, StudentId), arbitrated by the database like every other per-student row here.
/// </summary>
public sealed class QuizExtension
{
    public Guid Id { get; set; }

    public Guid QuizId { get; set; }
    public Guid StudentId { get; set; }

    /// <summary>When this student's quiz closes. Never earlier than the class deadline.</summary>
    public DateTime ClosesAtUtc { get; set; }

    public DateTime GrantedAtUtc { get; set; }

    public Quiz Quiz { get; set; } = null!;
}
