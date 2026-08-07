using StreamingService.Application.Abstractions;
using StreamingService.Application.Services;
using StreamingService.Domain.Entities;
using StreamingService.Domain.Enums;

namespace StreamingService.UnitTests;

/// <summary>
/// Joining and leaving a live stream, and the number the class is told (test-plan L-22..L-24).
///
/// Neither method had a service-level test. `StreamServiceRecordingToggleTests` passes
/// `participantRepository: null!` because it never reaches this code, and
/// `RecordingStreamHubContext.NotifyParticipantCountAsync` **discarded its argument** — so no test
/// could have failed on the count being wrong, and the count was wrong.
///
/// Both broadcasts derived it arithmetically from a collection loaded before the write:
///
///     await _hubContext.NotifyParticipantCountAsync(sessionId, stream.Participants.Count + 1);
///
/// The stream is read at the top of the request; the number is announced at the bottom. Anything
/// that happened in between is invisible to it. Two people joining at once both read the same
/// starting figure and both announce it plus one, so the class is told there are fewer people
/// present than there are — and nothing recomputes it until somebody else joins or leaves. The
/// leave path had the mirror image, with a `Math.Max(0, ...)` guarding against the negative number
/// that only arithmetic on a stale read can produce.
///
/// A unique index does not fix this one. The count has to be counted.
/// </summary>
public sealed class StreamJoinLeaveTests
{
    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly Guid StreamId = Guid.NewGuid();
    private static readonly Guid ClassroomId = Guid.NewGuid();
    private static readonly Guid Joiner = Guid.NewGuid();

    /// <summary>Admits the joiner and anyone the helper seeds; refuses everyone else by default.</summary>
    private static readonly FakeClassroomInternalClient Classrooms =
        new FakeClassroomInternalClient().Member(ClassroomId, Joiner);

    // --- joining ------------------------------------------------------------------------------

    [Fact]
    public async Task A_join_announces_the_number_of_people_actually_present()
    {
        var (service, participants, hub, _) = Build(alreadyPresent: 2);

        await service.JoinStreamAsync(SessionId, Joiner, default);

        Assert.Equal(3, participants.Rows.Count);
        var (session, count) = Assert.Single(hub.ParticipantCounts);
        Assert.Equal(SessionId, session);
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task The_announced_number_includes_people_who_arrived_since_this_request_started()
    {
        // The defect, reproduced exactly. The stream was loaded when one person was present;
        // two more have joined since. The old arithmetic announced `loaded + 1` = 2 while four
        // people were in the room, and nothing corrected it until the next join or leave.
        var (service, participants, hub, _) = Build(alreadyPresent: 1);
        participants.Rows.Add(Participant(Guid.NewGuid()));
        participants.Rows.Add(Participant(Guid.NewGuid()));

        await service.JoinStreamAsync(SessionId, Joiner, default);

        Assert.Equal(4, Assert.Single(hub.ParticipantCounts).Count);
    }

    [Fact]
    public async Task Joining_twice_adds_nothing_and_announces_nothing()
    {
        // The check the unique index now backs. A reconnect must be a no-op, not a second row and
        // not a second broadcast — a count that "changed" to the same value still redraws the
        // roster for everyone in the lecture.
        var (service, participants, hub, stream) = Build(alreadyPresent: 0);
        await service.JoinStreamAsync(SessionId, Joiner, default);
        hub.ParticipantCounts.Clear();

        // Each request re-reads the stream with its participants, so the second one sees the row
        // the first wrote. Modelled explicitly because the fake hands back the same entity, and
        // leaving the snapshot stale would test a request that cannot happen.
        stream.Participants = [.. participants.Rows];

        await service.JoinStreamAsync(SessionId, Joiner, default);

        Assert.Single(participants.Rows);
        Assert.Empty(hub.ParticipantCounts);
    }

    [Fact]
    public async Task Joining_a_stream_that_is_not_live_is_refused()
    {
        // The guard ahead of all of this: an ended session must not accept participants, or the
        // roster of a finished lecture keeps growing.
        var (service, participants, _, _) = Build(alreadyPresent: 0, status: StreamStatus.Ended);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.JoinStreamAsync(SessionId, Joiner, default));
        Assert.Empty(participants.Rows);
    }

    // --- leaving ------------------------------------------------------------------------------

    [Fact]
    public async Task A_leave_announces_the_number_of_people_actually_present()
    {
        var (service, participants, hub, _) = Build(alreadyPresent: 3);
        var leaver = participants.Rows[0];

        await service.LeaveStreamAsync(SessionId, leaver.UserId, default);

        Assert.Equal(2, participants.Rows.Count);
        Assert.Equal(2, Assert.Single(hub.ParticipantCounts).Count);
    }

    [Fact]
    public async Task The_last_person_leaving_announces_zero()
    {
        // What `Math.Max(0, ...)` was defending against. A real count cannot go negative, and the
        // last person leaving is an ordinary end to a lecture rather than an edge case.
        var (service, participants, hub, _) = Build(alreadyPresent: 1);

        await service.LeaveStreamAsync(SessionId, participants.Rows[0].UserId, default);

        Assert.Empty(participants.Rows);
        Assert.Equal(0, Assert.Single(hub.ParticipantCounts).Count);
    }

    [Fact]
    public async Task Leaving_when_you_were_never_there_changes_nothing()
    {
        var (service, participants, hub, _) = Build(alreadyPresent: 2);

        await service.LeaveStreamAsync(SessionId, Guid.NewGuid(), default);

        Assert.Equal(2, participants.Rows.Count);
        Assert.Empty(hub.ParticipantCounts);
    }

    // --- helpers ------------------------------------------------------------------------------

    private static StreamParticipant Participant(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        StreamId = StreamId,
        UserId = userId,
        JoinedAtUtc = DateTime.UtcNow,
    };

    private static (StreamService Service, TrackingParticipantRepository Participants, RecordingStreamHubContext Hub, LiveStream Stream)
        Build(int alreadyPresent, StreamStatus status = StreamStatus.Live)
    {
        var participants = new TrackingParticipantRepository();
        for (var i = 0; i < alreadyPresent; i++)
        {
            participants.Rows.Add(Participant(Guid.NewGuid()));
        }

        var stream = new LiveStream
        {
            Id = StreamId,
            SessionId = SessionId,
            ClassroomId = ClassroomId,
            TeacherId = Guid.NewGuid(),
            Status = status,
            StreamKey = "key",
            // What THIS request loaded — a snapshot, deliberately separable from the rows above.
            Participants = [.. participants.Rows],
        };

        var hub = new RecordingStreamHubContext();
        var service = new StreamService(
            new FakeStreamRepository(stream),
            participants,
            hub,
            mediaProvider: null!,
            new FakeRoomLifecycleService(),
            recordingStarter: null!,
            recordingEgress: null!,
            settings: null!,
            mediaSettings: null!,
            // Everyone these tests join as is a member; the refusals are StreamJoinAuthorizationTests'.
            Classrooms,
            new RecordingLogger<StreamService>());

        return (service, participants, hub, stream);
    }
}
