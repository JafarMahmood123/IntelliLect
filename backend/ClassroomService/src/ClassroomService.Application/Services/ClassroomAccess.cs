using ClassroomService.Application.Abstractions;
using ClassroomService.Application.Exceptions;
using ClassroomService.Domain.Entities;

namespace ClassroomService.Application.Services;

/// <summary>
/// Who may read a classroom, and who may act on it. One definition, because there were **five**
/// (§11.7's lesson at scale).
///
/// <see cref="QuizService"/>, <see cref="ClassroomFileService"/>, <see cref="ClassroomQaService"/>,
/// <see cref="ClassroomRecordingService"/> and <see cref="ClassroomSummaryService"/> each carried a
/// private <c>EnsureMemberAsync</c>. All five were byte-identical, which is the good case and not a
/// safe one: the next reader to change the rule — to count an admin, say, or to stop treating a
/// missing classroom as 404 — changes the copy in front of them, and the other four keep enforcing
/// the old one with nothing anywhere reporting the disagreement. §11.7 spent two surviving
/// mutations learning that with two copies of the quiz deadline. This is the same thing five times.
///
/// The rule takes its repositories as arguments rather than being injected, so adopting it costs no
/// constructor change in the five services that already hold both ports.
/// </summary>
public static class ClassroomAccess
{
    /// <summary>
    /// The caller is the classroom's teacher, or is enrolled in it. Anything else is refused.
    ///
    /// A missing classroom is 404 and a non-member is 403, in that order and before any other
    /// lookup — so the shape of the answer never depends on what is inside a classroom the caller
    /// may not see.
    /// </summary>
    public static async Task EnsureMemberAsync(
        IClassroomRepository classrooms,
        IMembershipRepository memberships,
        Guid classroomId,
        Guid userId,
        CancellationToken ct)
    {
        var classroom = await classrooms.GetByIdAsync(classroomId, ct)
            ?? throw new KeyNotFoundException("Classroom not found.");

        var isMember = classroom.TeacherId == userId
            || await memberships.IsEnrolledAsync(classroomId, userId, ct);

        if (!isMember)
        {
            throw new ForbiddenAccessException("You are not a member of this classroom.");
        }
    }

    /// <summary>
    /// The caller owns this classroom. Returns it, since every caller needs it next.
    ///
    /// Holding the Teacher role is not this: a role says what kind of user someone is, never which
    /// classroom is theirs. Every route that acted on a classroom on the strength of the role alone
    /// let any teacher in the platform act on any other teacher's class.
    /// </summary>
    public static async Task<Classroom> EnsureTeacherAsync(
        IClassroomRepository classrooms,
        Guid classroomId,
        Guid userId,
        CancellationToken ct)
    {
        var classroom = await classrooms.GetByIdAsync(classroomId, ct)
            ?? throw new KeyNotFoundException("Classroom not found.");

        if (classroom.TeacherId != userId)
        {
            throw new ForbiddenAccessException("Only the classroom's teacher can do this.");
        }

        return classroom;
    }
}
