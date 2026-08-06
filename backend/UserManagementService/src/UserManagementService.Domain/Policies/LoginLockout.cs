namespace UserManagementService.Domain.Policies;

/// <summary>
/// How many wrong passwords an account tolerates before it stops answering, and for how long.
///
/// The rule lives here rather than inside <c>User</c> so it is written exactly once. The same
/// boundary is asked about from two directions — "should this attempt be refused?" and "has the
/// lock lifted yet?" — and a rule written twice is a rule that eventually disagrees with itself.
/// </summary>
public static class LoginLockout
{
    /// <summary>Wrong passwords in a row before the account locks.</summary>
    public const int MaxFailedAttempts = 5;

    /// <summary>
    /// How long the lock holds. Short on purpose: it has to cost a brute-force attempt real time
    /// (five guesses per quarter hour is not a viable attack) without leaving a real user shut
    /// out of their own account until an administrator intervenes. Nobody is watching a queue of
    /// unlock requests on this system, so a lock that needed one would in practice be permanent.
    /// </summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Whether the account is refusing sign-in attempts at <paramref name="nowUtc"/>.
    ///
    /// The boundary is exclusive: at the instant the lock is due to end it has ended. Which side
    /// owns that instant matters less than that only one line decides it.
    /// </summary>
    public static bool IsLockedOut(DateTime? lockoutEndsAtUtc, DateTime nowUtc)
        => lockoutEndsAtUtc is { } endsAt && nowUtc < endsAt;
}
