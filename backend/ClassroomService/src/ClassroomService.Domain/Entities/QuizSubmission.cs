namespace ClassroomService.Domain.Entities;

/// <summary>
/// A student declaring they have finished a quiz, before its timer runs out.
///
/// Separate from <see cref="QuizAnswer"/> because finishing is a fact about the STUDENT and the
/// QUIZ, not about any one answer — a student who skipped half the questions has still finished,
/// and a flag on each answer row could not say so.
///
/// Its existence is what freezes that student's answers: answers stay changeable right up until
/// the quiz closes, which is exactly what a student wants until the moment they decide they are
/// done. Unique on (QuizId, StudentId), arbitrated by the database for the same reason
/// <see cref="QuizAnswer"/> is — a "have they submitted?" read is a race that two concurrent
/// clicks both pass.
///
/// Submitting is NOT required for marks. A student who runs out of time keeps everything they
/// answered; this only lets them stop early and lets the teacher see that they have.
/// </summary>
public sealed class QuizSubmission
{
    public Guid Id { get; set; }

    public Guid QuizId { get; set; }
    public Guid StudentId { get; set; }

    /// <summary>Captured from the token, for the same reason as <see cref="QuizAnswer.StudentName"/>.</summary>
    public string StudentName { get; set; } = string.Empty;

    public DateTime SubmittedAtUtc { get; set; }

    public Quiz Quiz { get; set; } = null!;
}
