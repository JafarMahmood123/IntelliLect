namespace StreamingService.Application.Abstractions;

/// <summary>
/// An authenticated user who is not permitted to do this — mapped to 403.
///
/// The distinction from <see cref="UnauthorizedAccessException"/> (401) is not pedantry here. The
/// front-end's axios interceptor treats a 401 as "the access token expired": it refreshes the
/// session, rotates the refresh token, and replays the request. So refusing a non-member with a 401
/// spends a token rotation on every refused join and then surfaces the same refusal anyway — and if
/// the refresh ever fails while that is happening, the user is signed out and sent to `/login` for
/// having clicked on a lecture they are not enrolled in.
///
/// ClassroomService has carried this exception since §7.2; StreamingService mapped every refusal to
/// 401 because it had nothing else to throw.
/// </summary>
public sealed class ForbiddenAccessException : Exception
{
    public ForbiddenAccessException(string message) : base(message) { }
}
