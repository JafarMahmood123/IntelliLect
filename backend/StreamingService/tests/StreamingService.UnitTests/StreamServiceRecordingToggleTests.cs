using StreamingService.Application.Abstractions;
using StreamingService.Application.Services;
using StreamingService.Domain.Entities;
using StreamingService.Domain.Enums;
using StreamingService.Infrastructure.Services;

namespace StreamingService.UnitTests;

/// <summary>
/// The teacher's in-session recording toggle. Recording is opt-in and stopping is FINAL — the
/// single-shot rule is what keeps an archived session one continuous video instead of fragments
/// that would need stitching, so it is enforced by the service rather than only by the UI.
/// </summary>
public sealed class StreamServiceRecordingToggleTests
{
    private static LiveStream Stream(
        Guid sessionId,
        Guid teacherId,
        RecordingState state = RecordingState.Off,
        string? egressId = null,
        StreamStatus status = StreamStatus.Live) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = sessionId,
        ClassroomId = Guid.NewGuid(),
        TeacherId = teacherId,
        Status = status,
        StreamKey = "k",
        RecordingState = state,
        EgressId = egressId,
    };

    /// <summary>
    /// The media provider/settings and the participant repository are genuinely untouched by the
    /// recording toggle — it reads the stream, writes a state, and calls egress — so they are left
    /// null rather than padded out with fakes that assert nothing.
    /// </summary>
    private static StreamService Create(
        FakeStreamRepository repo,
        FakeRecordingEgressService egress,
        RecordingStreamHubContext hub)
        => new(
            repo,
            participantRepository: null!,
            hub,
            mediaProvider: null!,
            new FakeRoomLifecycleService(),
            new RecordingStarter(repo, egress, new RecordingLogger<RecordingStarter>()),
            egress,
            settings: null!,
            mediaSettings: null!,
            // Refuses everybody, and the recording toggle passes anyway: it is a teacher action on
            // a stream the caller already owns, not a request to enter the room. If a membership
            // check ever appears on this path these tests will say so rather than sail through.
            new FakeClassroomInternalClient(),
            new RecordingLogger<StreamService>());

    // --- starting ---------------------------------------------------------------

    [Fact]
    public async Task Starting_recording_persists_the_state_and_starts_the_egress()
    {
        var sessionId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var repo = new FakeStreamRepository(Stream(sessionId, teacherId));
        var egress = new FakeRecordingEgressService(egressId: "EG_toggled_on");
        var hub = new RecordingStreamHubContext();

        var result = await Create(repo, egress, hub).UpdateRecordingStateAsync(sessionId, teacherId, true);

        Assert.Equal("Recording", result.State);
        Assert.Equal(RecordingState.Recording, repo.Find(sessionId)!.RecordingState);
        Assert.Equal(1, egress.StartCalls);
        Assert.Equal("EG_toggled_on", repo.Find(sessionId)!.EgressId);
    }

    [Fact]
    public async Task Starting_recording_tells_everyone_in_the_room()
    {
        // Students are entitled to know they are being recorded, so this is a room-wide broadcast
        // rather than a teacher-only UI update.
        var sessionId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var repo = new FakeStreamRepository(Stream(sessionId, teacherId));
        var hub = new RecordingStreamHubContext();

        await Create(repo, new FakeRecordingEgressService(egressId: "EG"), hub)
            .UpdateRecordingStateAsync(sessionId, teacherId, true);

        Assert.Equal((sessionId, "Recording"), Assert.Single(hub.RecordingStateChanges));
    }

    [Fact]
    public async Task The_desired_state_survives_LiveKit_rejecting_the_start()
    {
        // The state is what the reconcile loop converges on, so it must be stored even when the
        // start fails — otherwise the retry that would have repaired this has nothing to act on.
        var sessionId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var repo = new FakeStreamRepository(Stream(sessionId, teacherId));
        var egress = new FakeRecordingEgressService(throwOnCall: true);

        var result = await Create(repo, egress, new RecordingStreamHubContext())
            .UpdateRecordingStateAsync(sessionId, teacherId, true);

        Assert.Equal("Recording", result.State);
        Assert.Equal(RecordingState.Recording, repo.Find(sessionId)!.RecordingState);
        // The claim was released, so the next reconcile pass can retry immediately.
        Assert.Null(repo.Find(sessionId)!.EgressId);
    }

    // --- stopping ---------------------------------------------------------------

    [Fact]
    public async Task Stopping_recording_marks_it_ended_and_stops_the_egress()
    {
        var sessionId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var repo = new FakeStreamRepository(
            Stream(sessionId, teacherId, RecordingState.Recording, egressId: "EG_running"));
        var egress = new FakeRecordingEgressService();

        var result = await Create(repo, egress, new RecordingStreamHubContext())
            .UpdateRecordingStateAsync(sessionId, teacherId, false);

        Assert.Equal("Ended", result.State);
        Assert.Equal(RecordingState.Ended, repo.Find(sessionId)!.RecordingState);
        Assert.Equal("EG_running", egress.LastStoppedEgressId);
    }

    [Fact]
    public async Task Stopping_mid_session_does_not_wait_for_finalization()
    {
        // The finalize wait exists at SESSION END only because the room is closed immediately
        // afterwards, destroying the composite source mid-mux. Here the room stays open, so making
        // the teacher's request block on the encode would be latency for nothing.
        var sessionId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var repo = new FakeStreamRepository(
            Stream(sessionId, teacherId, RecordingState.Recording, egressId: "EG_running"));
        var egress = new FakeRecordingEgressService();

        await Create(repo, egress, new RecordingStreamHubContext())
            .UpdateRecordingStateAsync(sessionId, teacherId, false);

        Assert.Equal(0, egress.FinalizeCalls);
    }

    [Fact]
    public async Task Stopping_still_ends_the_recording_when_LiveKit_is_unreachable()
    {
        // Best-effort: the stored state is already Ended, so the reconcile loop stops whatever is
        // actually still running. Failing the teacher's request here would be a lie — the decision
        // has been recorded.
        var sessionId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var repo = new FakeStreamRepository(
            Stream(sessionId, teacherId, RecordingState.Recording, egressId: "EG_running"));

        var result = await Create(repo, new FakeRecordingEgressService(throwOnCall: true),
                new RecordingStreamHubContext())
            .UpdateRecordingStateAsync(sessionId, teacherId, false);

        Assert.Equal("Ended", result.State);
        Assert.Equal(RecordingState.Ended, repo.Find(sessionId)!.RecordingState);
    }

    // --- the single-shot rule ---------------------------------------------------

    [Fact]
    public async Task Recording_cannot_be_restarted_once_stopped()
    {
        var sessionId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var repo = new FakeStreamRepository(
            Stream(sessionId, teacherId, RecordingState.Ended, egressId: "EG_done"));
        var egress = new FakeRecordingEgressService(egressId: "EG_second");

        // InvalidOperationException is what the global handler maps to 409 Conflict.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(repo, egress, new RecordingStreamHubContext())
                .UpdateRecordingStateAsync(sessionId, teacherId, true));

        Assert.Equal(0, egress.StartCalls);
        Assert.Equal(RecordingState.Ended, repo.Find(sessionId)!.RecordingState);
    }

    [Fact]
    public async Task Stopping_a_session_that_never_recorded_does_not_burn_its_one_chance()
    {
        // "Off" and "Ended" both mean not recording, but only Ended is terminal. Turning off
        // something that never started must leave the teacher able to start it later.
        var sessionId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var repo = new FakeStreamRepository(Stream(sessionId, teacherId));
        var egress = new FakeRecordingEgressService();

        var result = await Create(repo, egress, new RecordingStreamHubContext())
            .UpdateRecordingStateAsync(sessionId, teacherId, false);

        Assert.Equal("Off", result.State);
        Assert.Equal(RecordingState.Off, repo.Find(sessionId)!.RecordingState);
        Assert.Equal(0, egress.StopCalls);
    }

    [Fact]
    public async Task Starting_an_already_recording_session_starts_nothing_new()
    {
        var sessionId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var repo = new FakeStreamRepository(
            Stream(sessionId, teacherId, RecordingState.Recording, egressId: "EG_running"));
        var egress = new FakeRecordingEgressService(egressId: "EG_second");

        var result = await Create(repo, egress, new RecordingStreamHubContext())
            .UpdateRecordingStateAsync(sessionId, teacherId, true);

        Assert.Equal("Recording", result.State);
        Assert.Equal(0, egress.StartCalls);
    }

    // --- authorization ----------------------------------------------------------

    [Fact]
    public async Task Only_the_sessions_own_teacher_can_change_recording()
    {
        // Defence in depth beneath the controller's [Authorize(Roles = "Teacher")]: another
        // teacher holds a valid Teacher token but has no business recording this room.
        var sessionId = Guid.NewGuid();
        var repo = new FakeStreamRepository(Stream(sessionId, Guid.NewGuid()));
        var egress = new FakeRecordingEgressService();

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => Create(repo, egress, new RecordingStreamHubContext())
                .UpdateRecordingStateAsync(sessionId, teacherId: Guid.NewGuid(), enabled: true));

        Assert.Equal(0, egress.StartCalls);
    }

    [Fact]
    public async Task Recording_cannot_be_changed_after_the_session_has_ended()
    {
        var sessionId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var repo = new FakeStreamRepository(
            Stream(sessionId, teacherId, status: StreamStatus.Ended));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(repo, new FakeRecordingEgressService(), new RecordingStreamHubContext())
                .UpdateRecordingStateAsync(sessionId, teacherId, true));
    }

    [Fact]
    public async Task An_unknown_session_is_not_found()
    {
        var repo = new FakeStreamRepository();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => Create(repo, new FakeRecordingEgressService(), new RecordingStreamHubContext())
                .UpdateRecordingStateAsync(Guid.NewGuid(), Guid.NewGuid(), true));
    }
}
