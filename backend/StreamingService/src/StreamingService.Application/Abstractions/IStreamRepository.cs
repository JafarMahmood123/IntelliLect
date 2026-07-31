using StreamingService.Domain.Entities;

namespace StreamingService.Application.Abstractions;

public interface IStreamRepository : IRepository<LiveStream>
{
    Task<LiveStream?> GetBySessionIdAsync(Guid sessionId, bool includeParticipants = false, CancellationToken ct = default);
    Task<bool> ExistsAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Correlates a LiveKit egress back to its stream (R-1 webhook path).</summary>
    Task<LiveStream?> GetByEgressIdAsync(string egressId, CancellationToken ct = default);

    /// <summary>
    /// All currently-live streams with their participants loaded. Backs the super-admin
    /// live-session monitor (participant count + whether recording is running).
    /// </summary>
    Task<List<LiveStream>> GetLiveStreamsAsync(CancellationToken ct = default);

    /// <summary>
    /// Atomically reserves the recording slot for a session by writing
    /// <paramref name="placeholderEgressId"/> only if no egress id is set yet. Returns whether
    /// THIS caller won.
    ///
    /// The database arbitrates because a read-then-write guard loses the race: two concurrent
    /// <c>room_started</c> webhooks both see a null egress id, both start a composite, and the
    /// host pays twice for a duplicate recording that nothing will ever clean up. Same idiom as
    /// ClassroomService's session end claim.
    /// </summary>
    Task<bool> TryClaimEgressSlotAsync(
        Guid sessionId, string placeholderEgressId, CancellationToken ct = default);

    /// <summary>
    /// Replaces a claim placeholder with the real egress id (or clears it back to <c>null</c> when
    /// the start failed, so the reconcile loop can retry).
    /// </summary>
    Task SetEgressIdAsync(Guid streamId, string? egressId, CancellationToken ct = default);

    /// <summary>
    /// Live streams the teacher wants recorded whose recording never started — the
    /// <c>room_started</c> webhook was missed, the toggle request was lost, a start failed, or a
    /// claim was abandoned. Sessions with recording off or already stopped are excluded: those are
    /// deliberate, not broken.
    ///
    /// <paramref name="claimedBeforeUtc"/> is a single staleness cutoff applied twice: the stream
    /// must have gone live before it (so the normal webhook path gets first chance, instead of
    /// this racing it and trying to attach to a room that does not exist yet), and a placeholder
    /// claim must predate it before it counts as abandoned rather than in flight.
    /// </summary>
    Task<List<LiveStream>> GetLiveStreamsNeedingRecordingAsync(
        string placeholderPrefix, DateTime claimedBeforeUtc, CancellationToken ct = default);
}