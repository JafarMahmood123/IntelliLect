namespace ClassroomService.Application.DTOs.Membership;

/// <summary>A student membership row (studentId + join date). Name/email are resolved by
/// UserManagementService, which owns the user store.</summary>
public sealed record ClassroomMemberRow(Guid StudentId, DateTime JoinedAtUtc);

/// <summary>
/// The full membership set of a classroom for the super-admin member view: the owning teacher plus
/// every enrolled student. Returned unpaged (a single classroom's roster is bounded); UMS enriches
/// with names/emails and does the search/paging.
/// </summary>
public sealed record ClassroomMembersResult(
    Guid ClassroomId,
    string ClassroomName,
    Guid TeacherId,
    IReadOnlyList<ClassroomMemberRow> Students);

/// <summary>
/// Outcome of an add/remove. <see cref="Changed"/> is false for the 5ج no-op (already a member).
/// </summary>
public sealed record MemberMutationResult(bool Changed, Guid ClassroomId, string ClassroomName, Guid StudentId);

/// <summary>
/// Whether one user may be in one classroom, for another service that has no roster of its own.
///
/// StreamingService issues the LiveKit join token, which IS the authorization for the media room —
/// once LiveKit holds it our code is never consulted again. It knows a stream's classroom and its
/// teacher and nothing about who is enrolled, so the question has to be asked here.
///
/// Both flags, not one: the teacher is entitled to the room without being an enrolled student, and
/// the caller needs to tell the two apart to decide publishing rights.
/// </summary>
public sealed record ClassroomAccessResult(
    Guid ClassroomId,
    Guid UserId,
    bool IsMember,
    bool IsTeacher);
