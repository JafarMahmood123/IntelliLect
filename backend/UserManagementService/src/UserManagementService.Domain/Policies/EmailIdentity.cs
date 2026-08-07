namespace UserManagementService.Domain.Policies;

/// <summary>
/// What it means for two people to have "the same" email address.
///
/// Registration asks the question — *is this address already taken?* — and until now it asked it
/// with `u.Email == email`, which in Postgres is an exact, case-SENSITIVE comparison. So
/// <c>Jafar@example.com</c> and <c>jafar@example.com</c> were two different accounts, and creating
/// the second needed no race and no unusual timing: just a capital letter.
///
/// The consequences all follow from the same place, because every lookup in the service goes
/// through the same comparison:
///
///   * The owner is signed in to whichever row their capitalisation happens to match, so typing
///     the address differently on the next visit is "invalid credentials" for an account that
///     plainly exists.
///   * A password reset finds no user for the other spelling and — correctly, per A-13 — answers
///     as though it had sent a code. It never arrives, and the wording is designed not to say why.
///   * An administrator approves one of the two rows. The person signs in to the other and is told
///     their account is pending, indefinitely, with an approved account sitting beside it.
///
/// Case-insensitivity is the right rule and not merely the convenient one. The domain part of an
/// address is case-insensitive by RFC 1035; the local part is technically case-sensitive by
/// RFC 5321, and no mail provider anyone uses has treated it that way in decades. Treating
/// <c>A@x.com</c> and <c>a@x.com</c> as one account is what every user already expects, and it is
/// the only reading under which "an address identifies a person" is true.
///
/// Trimming is here for the same reason: a trailing space from a paste or an autofill is not a
/// different person.
/// </summary>
public static class EmailIdentity
{
    /// <summary>
    /// The canonical form of an address — the value stored, and the value every lookup compares.
    ///
    /// Invariant lowercase, not the current culture's: `ToLower()` under a Turkish locale maps
    /// <c>I</c> to <c>ı</c>, so the same address would normalise differently depending on the
    /// server's regional settings and an account could become unreachable by moving the container.
    /// </summary>
    public static string Normalize(string? email)
        => (email ?? string.Empty).Trim().ToLowerInvariant();
}
