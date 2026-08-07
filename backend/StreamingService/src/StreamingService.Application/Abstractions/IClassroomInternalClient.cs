namespace StreamingService.Application.Abstractions;

/// <summary>
/// Asks ClassroomService whether a user belongs to a classroom (test-plan G-02).
///
/// This service holds a stream's <c>ClassroomId</c> and <c>TeacherId</c> and no roster at all, so
/// "is this person in this class?" cannot be answered locally. It matters more here than almost
/// anywhere else: the LiveKit join token IS the authorization for the media room, and once LiveKit
/// holds it our code is never consulted again.
///
/// Unlike <see cref="ILiveAssistantInternalClient"/>, this is NOT best-effort. The assistant is an
/// enhancement and a failed call there costs a feature; a failed call here is an unanswered
/// authorization question, and the only safe answer to that is no.
/// </summary>
public interface IClassroomInternalClient
{
    /// <summary>
    /// Whether <paramref name="userId"/> may be in <paramref name="classroomId"/>.
    /// </summary>
    /// <returns>
    /// The classroom's answer. Never null — an unknown classroom, an unreachable service and a
    /// malformed reply all come back as <see cref="ClassroomAccess.None"/>, because a caller that
    /// has to distinguish "no" from "could not ask" in order to be safe will eventually get it
    /// wrong. The distinction is preserved in the log, where it belongs.
    /// </returns>
    Task<ClassroomAccess> GetAccessAsync(Guid classroomId, Guid userId, CancellationToken ct = default);
}

/// <summary>What ClassroomService says about one person and one classroom.</summary>
public readonly record struct ClassroomAccess(bool IsMember, bool IsTeacher)
{
    /// <summary>Not a member, and not the teacher. Also what an unanswerable question returns.</summary>
    public static ClassroomAccess None => new(false, false);
}
