using System.Globalization;

namespace StreamingService.Infrastructure.Services;

/// <summary>
/// The placeholder written into <c>LiveStream.EgressId</c> to reserve a session's recording slot
/// before LiveKit is called.
///
/// Claiming BEFORE the start call is what makes the reservation atomic — but it also means a crash
/// between the claim and the real id would strand the row forever. The claim therefore carries its
/// own timestamp, so the reconcile loop can tell a claim that is merely in flight from one that was
/// abandoned. Encoding it in the value keeps this migration-free.
/// </summary>
public static class EgressClaim
{
    /// <summary>Marks a value as a claim rather than a real LiveKit egress id.</summary>
    public const string Prefix = "pending:";

    public static string New(DateTime utcNow)
        => Prefix + utcNow.Ticks.ToString(CultureInfo.InvariantCulture);

    public static bool IsClaim(string? egressId)
        => egressId is not null && egressId.StartsWith(Prefix, StringComparison.Ordinal);
}
