using ClassroomService.Domain.Enums;

namespace ClassroomService.Domain.Entities;

/// <summary>
/// A quiz the teacher composes and publishes during a live session. Owned by ClassroomService
/// rather than StreamingService because the marks it produces are academic records: they have to
/// outlive the LiveStream that happened to be running, and be queried per student across sessions.
/// </summary>
public sealed class Quiz
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid ClassroomId { get; set; }
    public Guid CreatedByTeacherId { get; set; }

    public string Title { get; set; } = string.Empty;
    public QuizStatus Status { get; set; } = QuizStatus.Draft;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PublishedAtUtc { get; set; }

    /// <summary>
    /// The deadline, stamped at publish from the sum of the questions' time limits. This is the
    /// AUTHORITY on whether the quiz still accepts answers — every submission checks it. The status
    /// field is flipped for the UI's benefit, but a missed flip must never leave a quiz scoring
    /// past its deadline, so nothing depends on that having happened.
    /// </summary>
    public DateTime? ClosesAtUtc { get; set; }

    public DateTime? ClosedAtUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }

    public ICollection<QuizQuestion> Questions { get; set; } = new List<QuizQuestion>();
}
