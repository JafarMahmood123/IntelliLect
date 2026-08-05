using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Logging.Abstractions;
using StreamingService.Infrastructure.Services;

namespace StreamingService.UnitTests;

/// <summary>
/// Server-side control over the media room itself, as opposed to the tokens that let people in.
/// It had no tests, because it built the LiveKit SDK client in its own constructor; it now takes
/// <see cref="ILiveKitRoomClient"/>, the same seam the egress path has used all along.
///
/// Two rules live here and nowhere else.
///
/// Closing the room is what actually removes people when a session ends. The "session ended"
/// broadcast is a courtesy — a tab that was asleep, or on a flaky connection, never sees it. If
/// the room is not deleted, that student stays connected to a live audio and video feed of a
/// classroom that, as far as everyone else is concerned, closed.
///
/// The publish policy is the mute switch. It is applied by ROLE, read from participant metadata,
/// and getting that wrong is not a subtle bug: revoking a source force-unpublishes it
/// immediately, so mistaking the teacher for a student cuts off the person giving the lecture,
/// mid-sentence, in front of the class.
/// </summary>
public sealed class LiveKitRoomLifecycleServiceTests
{
    private static readonly Guid SessionId = Guid.NewGuid();

    /// <summary>Records what was asked of LiveKit, and can be told to fail on any of it.</summary>
    private sealed class FakeRoomClient : ILiveKitRoomClient
    {
        public List<string> DeletedRooms { get; } = new();
        public List<UpdateParticipantRequest> Updates { get; } = new();
        public List<ParticipantInfo> Participants { get; } = new();

        public Exception? DeleteThrows;
        public Exception? ListThrows;
        /// <summary>Identity whose update fails — the "student just disconnected" case.</summary>
        public string? UpdateThrowsFor;

        public Task<DeleteRoomResponse> DeleteRoomAsync(DeleteRoomRequest request)
        {
            if (DeleteThrows is not null) throw DeleteThrows;
            DeletedRooms.Add(request.Room);
            return Task.FromResult(new DeleteRoomResponse());
        }

        public Task<ListParticipantsResponse> ListParticipantsAsync(ListParticipantsRequest request)
        {
            if (ListThrows is not null) throw ListThrows;
            var response = new ListParticipantsResponse();
            response.Participants.AddRange(Participants);
            return Task.FromResult(response);
        }

        public Task<ParticipantInfo> UpdateParticipantAsync(UpdateParticipantRequest request)
        {
            if (UpdateThrowsFor is not null && request.Identity == UpdateThrowsFor)
            {
                throw new InvalidOperationException("participant is gone");
            }
            Updates.Add(request);
            return Task.FromResult(new ParticipantInfo { Identity = request.Identity });
        }
    }

    private static LiveKitRoomLifecycleService Build(FakeRoomClient client)
        => new(client, NullLogger<LiveKitRoomLifecycleService>.Instance);

    /// <summary>A participant carrying the metadata LiveKitMediaProvider actually writes.</summary>
    private static ParticipantInfo Participant(string identity, string? role)
        => new()
        {
            Identity = identity,
            Metadata = role is null ? string.Empty : $"{{\"role\":\"{role}\"}}",
        };

    private static UpdateParticipantRequest UpdateFor(FakeRoomClient client, string identity)
        => Assert.Single(client.Updates, u => u.Identity == identity);

    // --- closing the room ---------------------------------------------------------

    [Fact]
    public async Task Closing_a_session_deletes_its_room()
    {
        var client = new FakeRoomClient();

        await Build(client).CloseRoomAsync(SessionId.ToString());

        Assert.Equal(SessionId.ToString(), Assert.Single(client.DeletedRooms));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Closing_without_a_room_name_asks_livekit_for_nothing(string roomName)
    {
        // A session that never started has no room. Sending a delete for "" would either error or,
        // worse, be interpreted as some other room.
        var client = new FakeRoomClient();

        await Build(client).CloseRoomAsync(roomName);

        Assert.Empty(client.DeletedRooms);
    }

    [Fact]
    public async Task A_failure_to_close_the_room_is_reported_to_the_caller()
    {
        // Deliberately NOT swallowed, unlike the policy path below. Silently "closing" a room that
        // is still live leaves students connected to a session everyone believes has ended, and
        // the caller is the only one positioned to retry or record that.
        var client = new FakeRoomClient { DeleteThrows = new HttpRequestException("livekit is down") };

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Build(client).CloseRoomAsync(SessionId.ToString()));
    }

    // --- the publish policy -------------------------------------------------------

    [Fact]
    public async Task Only_students_have_their_publish_permissions_changed()
    {
        // The teacher and the AI assistant are in the same room. Revoking a source
        // force-unpublishes it immediately, so touching the teacher here silences the lecture.
        var client = new FakeRoomClient();
        client.Participants.AddRange([
            Participant("student-1", "Student"),
            Participant("teacher-1", "Teacher"),
            Participant("assistant", "Assistant"),
        ]);

        await Build(client).ApplyStudentPublishPolicyAsync(SessionId, true, true);

        Assert.Equal(["student-1"], client.Updates.Select(u => u.Identity));
    }

    [Fact]
    public async Task The_role_match_is_case_insensitive()
    {
        // The role is written by another part of the system and read back as a string; a casing
        // change upstream must not quietly stop the mute switch reaching anyone.
        var client = new FakeRoomClient();
        client.Participants.Add(Participant("student-1", "student"));

        await Build(client).ApplyStudentPublishPolicyAsync(SessionId, false, false);

        Assert.Single(client.Updates);
    }

    [Theory]
    [InlineData(null)]           // no metadata at all
    [InlineData("not json")]     // malformed
    [InlineData("{}")]           // valid JSON, no role
    public async Task A_participant_whose_role_cannot_be_read_is_left_alone(string? metadata)
    {
        // Fail toward doing nothing. An unreadable role is not evidence of a student, and acting
        // on the guess would cut off whoever it actually was.
        var client = new FakeRoomClient();
        client.Participants.Add(new ParticipantInfo { Identity = "mystery", Metadata = metadata ?? string.Empty });

        await Build(client).ApplyStudentPublishPolicyAsync(SessionId, false, false);

        Assert.Empty(client.Updates);
    }

    [Fact]
    public async Task Muting_students_revokes_publishing_entirely_rather_than_leaving_an_empty_list()
    {
        // Both halves are needed. `CanPublish = true` with no sources, or a source list with the
        // master switch still on, each leaves a way back in — the same pairing the join token
        // tests pin for a view-only student.
        var client = new FakeRoomClient();
        client.Participants.Add(Participant("student-1", "Student"));

        await Build(client).ApplyStudentPublishPolicyAsync(SessionId, false, false);

        var permission = UpdateFor(client, "student-1").Permission;
        Assert.False(permission.CanPublish);
        Assert.Empty(permission.CanPublishSources);
    }

    [Theory]
    [InlineData(true, false, new[] { TrackSource.Microphone })]
    [InlineData(false, true, new[] { TrackSource.Camera })]
    [InlineData(true, true, new[] { TrackSource.Camera, TrackSource.Microphone })]
    public async Task Each_granted_source_is_the_only_one_granted(
        bool audio, bool video, TrackSource[] expected)
    {
        // "Cameras on, microphones off" has to mean exactly that: granting audio must not drag
        // video along, and neither may quietly include screen-share, which is the teacher's.
        var client = new FakeRoomClient();
        client.Participants.Add(Participant("student-1", "Student"));

        await Build(client).ApplyStudentPublishPolicyAsync(SessionId, audio, video);

        var permission = UpdateFor(client, "student-1").Permission;
        Assert.True(permission.CanPublish);
        Assert.Equal(expected, permission.CanPublishSources);
    }

    [Fact]
    public async Task A_muted_student_can_still_hear_the_class_and_use_the_chat()
    {
        // Muting is not ejection. Subscribe carries the lecture audio and video; data carries the
        // chat and the quiz. Dropping either turns "you may not speak" into "you may not attend".
        var client = new FakeRoomClient();
        client.Participants.Add(Participant("student-1", "Student"));

        await Build(client).ApplyStudentPublishPolicyAsync(SessionId, false, false);

        var permission = UpdateFor(client, "student-1").Permission;
        Assert.True(permission.CanSubscribe);
        Assert.True(permission.CanPublishData);
    }

    [Fact]
    public async Task The_policy_is_applied_in_the_session_s_own_room()
    {
        var client = new FakeRoomClient();
        client.Participants.Add(Participant("student-1", "Student"));

        await Build(client).ApplyStudentPublishPolicyAsync(SessionId, true, false);

        Assert.Equal(SessionId.ToString(), UpdateFor(client, "student-1").Room);
    }

    [Fact]
    public async Task Every_student_in_the_room_is_updated()
    {
        var client = new FakeRoomClient();
        client.Participants.AddRange([
            Participant("student-1", "Student"),
            Participant("student-2", "Student"),
            Participant("student-3", "Student"),
        ]);

        await Build(client).ApplyStudentPublishPolicyAsync(SessionId, false, false);

        Assert.Equal(3, client.Updates.Count);
    }

    // --- when LiveKit does not cooperate ------------------------------------------

    [Fact]
    public async Task One_student_that_cannot_be_updated_does_not_stop_the_others()
    {
        // Participants disconnect while the loop is running — routine, not exceptional. Letting
        // that abort the sweep would leave everyone after them still publishing.
        var client = new FakeRoomClient { UpdateThrowsFor = "student-2" };
        client.Participants.AddRange([
            Participant("student-1", "Student"),
            Participant("student-2", "Student"),
            Participant("student-3", "Student"),
        ]);

        await Build(client).ApplyStudentPublishPolicyAsync(SessionId, false, false);

        Assert.Equal(["student-1", "student-3"], client.Updates.Select(u => u.Identity));
    }

    [Fact]
    public async Task A_room_that_does_not_exist_yet_is_not_an_error()
    {
        // The teacher sets the policy before anyone joins, so there is no room to list. The
        // persisted policy and the join token still cover whoever arrives later — this call is
        // only about people already connected.
        var client = new FakeRoomClient { ListThrows = new InvalidOperationException("room not found") };

        await Build(client).ApplyStudentPublishPolicyAsync(SessionId, false, false);

        Assert.Empty(client.Updates);
    }

    [Fact]
    public async Task An_empty_room_is_a_no_op()
    {
        var client = new FakeRoomClient();

        await Build(client).ApplyStudentPublishPolicyAsync(SessionId, false, false);

        Assert.Empty(client.Updates);
    }
}
