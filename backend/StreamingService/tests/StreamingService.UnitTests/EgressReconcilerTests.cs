using Microsoft.Extensions.Logging.Abstractions;
using StreamingService.Domain.Entities;
using StreamingService.Domain.Enums;
using StreamingService.Infrastructure.Services;

namespace StreamingService.UnitTests;

/// <summary>
/// The reconcile pass is the safety net for the fact that recording is otherwise driven by ONE
/// unretried <c>room_started</c> webhook: a missed delivery loses a whole lecture's recording
/// silently, and this stack restarts often enough to hit that window.
/// </summary>
public sealed class EgressReconcilerTests
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    // Defaults to Recording because that is what these tests are about: a session the teacher DID
    // ask to record. Recording is opt-in, so the entity's own default is Off.
    private static LiveStream LiveStreamStartedMinutesAgo(
        int minutes,
        string? egressId = null,
        RecordingState recordingState = RecordingState.Recording) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = Guid.NewGuid(),
        ClassroomId = Guid.NewGuid(),
        TeacherId = Guid.NewGuid(),
        StreamKey = "key",
        Status = StreamStatus.Live,
        StartedAtUtc = DateTime.UtcNow.AddMinutes(-minutes),
        EgressId = egressId,
        RecordingState = recordingState,
    };

    // Uses the REAL starter over the fakes rather than a stub, so these tests keep covering the
    // claim arbitration that moved into it.
    private static EgressReconciler Create(
        FakeStreamRepository streams, FakeRecordingEgressService egress)
        => new(
            streams,
            egress,
            new RecordingStarter(streams, egress, NullLogger<RecordingStarter>.Instance),
            NullLogger<EgressReconciler>.Instance);

    // --- direction 1: start what is missing ------------------------------------

    [Fact]
    public async Task Starts_recording_for_a_live_session_whose_webhook_never_produced_one()
    {
        var stream = LiveStreamStartedMinutesAgo(5);
        var streams = new FakeStreamRepository(stream);
        var egress = new FakeRecordingEgressService(egressId: "EG_recovered");

        await Create(streams, egress).ReconcileAsync(StaleAfter);

        Assert.Equal(1, egress.StartCalls);
        // Room name is the session id, matching LiveKitMediaProvider's convention.
        Assert.Equal(stream.SessionId.ToString(), egress.LastRoomName);
        // The real egress id replaced the claim placeholder.
        Assert.Equal("EG_recovered", streams.Find(stream.SessionId)!.EgressId);
    }

    [Fact]
    public async Task Never_records_a_session_the_teacher_did_not_ask_to_record()
    {
        // The rule this pass used to encode — "live and not recording means something broke" — is
        // wrong now that recording is opt-in. Getting this backwards would record every session
        // whose teacher deliberately left it off.
        var streams = new FakeStreamRepository(
            LiveStreamStartedMinutesAgo(5, recordingState: RecordingState.Off));
        var egress = new FakeRecordingEgressService(egressId: "EG_unwanted");

        await Create(streams, egress).ReconcileAsync(StaleAfter);

        Assert.Equal(0, egress.StartCalls);
    }

    [Fact]
    public async Task Never_restarts_a_recording_the_teacher_stopped()
    {
        // Stopping is final. Restarting here would both defy the teacher and split the session
        // into two files.
        var streams = new FakeStreamRepository(
            LiveStreamStartedMinutesAgo(5, recordingState: RecordingState.Ended));
        var egress = new FakeRecordingEgressService(egressId: "EG_restarted");

        await Create(streams, egress).ReconcileAsync(StaleAfter);

        Assert.Equal(0, egress.StartCalls);
    }

    [Fact]
    public async Task Stops_a_recording_the_teacher_turned_off_while_the_session_is_still_live()
    {
        // The toggle is a single unretried HTTP request; if its StopEgress call was lost, the
        // session would keep recording after the teacher was told it had stopped.
        var stream = LiveStreamStartedMinutesAgo(
            5, egressId: "EG_should_be_stopped", recordingState: RecordingState.Ended);
        var streams = new FakeStreamRepository(stream);
        var egress = new FakeRecordingEgressService
        {
            ActiveEgressIds = new HashSet<string> { "EG_should_be_stopped" },
        };

        await Create(streams, egress).ReconcileAsync(StaleAfter);

        Assert.Equal(1, egress.StopCalls);
        Assert.Equal("EG_should_be_stopped", egress.LastStoppedEgressId);
    }

    [Fact]
    public async Task Leaves_a_running_recording_alone_while_it_is_still_wanted()
    {
        var stream = LiveStreamStartedMinutesAgo(5, egressId: "EG_wanted");
        var streams = new FakeStreamRepository(stream);
        var egress = new FakeRecordingEgressService
        {
            ActiveEgressIds = new HashSet<string> { "EG_wanted" },
        };

        await Create(streams, egress).ReconcileAsync(StaleAfter);

        Assert.Equal(0, egress.StopCalls);
    }

    [Fact]
    public async Task Leaves_a_freshly_live_session_alone_so_the_webhook_gets_first_chance()
    {
        // room_started is still in flight; attaching egress to a room that does not exist yet
        // would only fail and waste a start call.
        var streams = new FakeStreamRepository(LiveStreamStartedMinutesAgo(0));
        var egress = new FakeRecordingEgressService();

        await Create(streams, egress).ReconcileAsync(StaleAfter);

        Assert.Equal(0, egress.StartCalls);
    }

    [Fact]
    public async Task Leaves_a_session_that_is_already_recording_alone()
    {
        var streams = new FakeStreamRepository(LiveStreamStartedMinutesAgo(5, egressId: "EG_running"));
        var egress = new FakeRecordingEgressService
        {
            ActiveEgressIds = new HashSet<string> { "EG_running" },
        };

        await Create(streams, egress).ReconcileAsync(StaleAfter);

        Assert.Equal(0, egress.StartCalls);
        Assert.Equal(0, egress.StopCalls);
    }

    [Fact]
    public async Task Reclaims_an_abandoned_claim_placeholder()
    {
        // A crash between claiming the slot and writing the real egress id would otherwise strand
        // the session unrecorded forever — the claim requires NULL, so it cannot recover itself.
        var stale = EgressClaim.New(DateTime.UtcNow.AddMinutes(-10));
        var stream = LiveStreamStartedMinutesAgo(15, egressId: stale);
        var streams = new FakeStreamRepository(stream);
        var egress = new FakeRecordingEgressService(egressId: "EG_retry");

        await Create(streams, egress).ReconcileAsync(StaleAfter);

        Assert.Equal(1, egress.StartCalls);
        Assert.Equal("EG_retry", streams.Find(stream.SessionId)!.EgressId);
    }

    [Fact]
    public async Task Does_not_steal_a_claim_that_is_still_in_flight()
    {
        var fresh = EgressClaim.New(DateTime.UtcNow);
        var streams = new FakeStreamRepository(LiveStreamStartedMinutesAgo(5, egressId: fresh));
        var egress = new FakeRecordingEgressService();

        await Create(streams, egress).ReconcileAsync(StaleAfter);

        Assert.Equal(0, egress.StartCalls);
    }

    [Fact]
    public async Task Releases_the_claim_when_starting_fails_so_the_next_pass_retries()
    {
        var stream = LiveStreamStartedMinutesAgo(5);
        var streams = new FakeStreamRepository(stream);
        var egress = new FakeRecordingEgressService(throwOnCall: true);

        await Create(streams, egress).ReconcileAsync(StaleAfter);

        Assert.Equal(1, egress.StartCalls);            // it did try
        Assert.Null(streams.Find(stream.SessionId)!.EgressId); // and left nothing stuck
    }

    [Fact]
    public async Task Releases_the_claim_when_recording_is_disabled()
    {
        // A null egress id means the feature flag is off; holding a placeholder would make the
        // session look permanently mid-claim.
        var stream = LiveStreamStartedMinutesAgo(5);
        var streams = new FakeStreamRepository(stream);
        var egress = new FakeRecordingEgressService(egressId: null);

        await Create(streams, egress).ReconcileAsync(StaleAfter);

        Assert.Null(streams.Find(stream.SessionId)!.EgressId);
    }

    // --- direction 2: stop what is orphaned ------------------------------------

    [Fact]
    public async Task Stops_an_egress_whose_session_has_already_ended()
    {
        var ended = LiveStreamStartedMinutesAgo(60, egressId: "EG_orphan");
        ended.Status = StreamStatus.Ended;
        var streams = new FakeStreamRepository(ended);
        var egress = new FakeRecordingEgressService
        {
            ActiveEgressIds = new HashSet<string> { "EG_orphan" },
        };

        await Create(streams, egress).ReconcileAsync(StaleAfter);

        Assert.Equal(1, egress.StopCalls);
        Assert.Equal("EG_orphan", egress.LastStoppedEgressId);
    }

    [Fact]
    public async Task Never_stops_an_egress_it_does_not_own()
    {
        // Stopping a stranger's recording is far worse than leaking one.
        var streams = new FakeStreamRepository();
        var egress = new FakeRecordingEgressService
        {
            ActiveEgressIds = new HashSet<string> { "EG_someone_else" },
        };

        await Create(streams, egress).ReconcileAsync(StaleAfter);

        Assert.Equal(0, egress.StopCalls);
    }

    // --- outage behaviour -------------------------------------------------------

    [Fact]
    public async Task Skips_the_whole_pass_when_livekit_state_cannot_be_read()
    {
        // Reading "unreachable" as "nothing is running" would start a duplicate recording for
        // every live session — the worst possible response to a transient outage.
        var streams = new FakeStreamRepository(LiveStreamStartedMinutesAgo(5));
        var egress = new FakeRecordingEgressService { ThrowOnGetActive = true };

        await Create(streams, egress).ReconcileAsync(StaleAfter);

        Assert.Equal(0, egress.StartCalls);
        Assert.Equal(0, egress.StopCalls);
    }
}
