using StreamingService.Application.Abstractions;
using StreamingService.Application.Services;
using StreamingService.Domain.Entities;
using StreamingService.Domain.Enums;

namespace StreamingService.UnitTests;

/// <summary>
/// What happens inside a live lecture, and who is allowed to do it — the surface §7.4d's token fix
/// did not reach (test-plan G-44..G-51).
///
/// `InteractionService` had **no tests at all**, which by now is a reliable signal rather than a
/// coincidence: every unguarded thing found in this sweep has also been the untested thing. Every
/// method on it checked that the stream was Live and nothing about the caller.
///
/// - **The three writes took a `userId` and never consulted it.** Any authenticated account could
///   post chat into any live lecture in the platform, from its session id — and chat is broadcast
///   to everyone in the room with the sender's display name against it. Reactions and questions the
///   same.
/// - **The two reads took no caller at all.** Any authenticated account could page through any
///   lecture's entire chat history and question list.
/// - **`StreamHub.JoinStreamRoom` put any connection into any session's broadcast group**, which is
///   the live feed of all of the above plus the participant count. A hub method is the public
///   surface: no controller in front of it, no filter that ever saw the session id.
///
/// `AnswerQuestionAsync` was already correct (`stream.TeacherId != teacherId`), and
/// `ToggleHandRaiseAsync` was already safe — but by accident rather than by decision: it requires a
/// participant row, and since §7.4d a row can only be created by a member. There is a test below
/// pinning that, because "safe because of something another method does" is worth knowing about.
/// </summary>
public sealed class StreamInteractionAuthorizationTests
{
    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly Guid StreamId = Guid.NewGuid();
    private static readonly Guid ClassroomId = Guid.NewGuid();
    private static readonly Guid Teacher = Guid.NewGuid();
    private static readonly Guid Student = Guid.NewGuid();
    private static readonly Guid Outsider = Guid.NewGuid();

    // --- the writes ------------------------------------------------------------------------------

    [Fact]
    public async Task A_stranger_cannot_post_chat_into_a_lecture()
    {
        // The most visible of the three: the message is broadcast to everyone in the room, with the
        // sender's name against it, while the class is running.
        var world = new World();

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => world.Service.SendChatMessageAsync(SessionId, Outsider, "Nobody", "hello", default));

        Assert.Empty(world.Chat.Rows);
        Assert.Empty(world.Hub.ChatMessages);
    }

    [Fact]
    public async Task A_stranger_cannot_react()
    {
        var world = new World();

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => world.Service.SendReactionAsync(SessionId, Outsider, "👏", default));

        Assert.Empty(world.Reactions.Rows);
    }

    [Fact]
    public async Task A_stranger_cannot_ask_a_question()
    {
        var world = new World();

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => world.Service.AskQuestionAsync(SessionId, Outsider, "Nobody", "why?", default));

        Assert.Empty(world.Questions.Rows);
    }

    [Fact]
    public async Task Someone_in_the_room_can_post_chat()
    {
        // The vacuum guard on all three refusals. A rule that refused everybody would pass every
        // test above and silence the lecture.
        var world = new World();
        world.PutInRoom(Student);

        await world.Service.SendChatMessageAsync(SessionId, Student, "Ammar", "hello", default);

        Assert.Single(world.Chat.Rows);
        Assert.Single(world.Hub.ChatMessages);
    }

    [Fact]
    public async Task An_enrolled_member_who_has_not_joined_yet_can_still_post()
    {
        // The fallback path, and it is not a nicety. The browser opens its SignalR connection in an
        // effect gated on the session id and the access token — it does not wait for `POST /join` —
        // so a rule that demanded the participant row alone would refuse legitimate users
        // intermittently, which is the worst way for an authorization rule to be wrong.
        var world = new World();
        world.Classrooms.Member(ClassroomId, Student);

        await world.Service.SendChatMessageAsync(SessionId, Student, "Ammar", "hello", default);

        Assert.Single(world.Chat.Rows);
    }

    [Fact]
    public async Task Being_in_the_room_costs_no_remote_call()
    {
        // Chat is the hot path here — one message per keystroke-burst, per person, per lecture. The
        // participant row is a local read and it already means "passed the membership check at
        // join", so the common case must not pay for a round trip to ClassroomService.
        var world = new World();
        world.PutInRoom(Student);

        await world.Service.SendChatMessageAsync(SessionId, Student, "Ammar", "hello", default);

        Assert.Empty(world.Classrooms.Asked);
    }

    [Fact]
    public async Task An_unreachable_classroom_service_refuses_someone_who_has_not_joined()
    {
        // Fail closed, and note what it does NOT break: everyone already in the room keeps talking,
        // because their participant row answers before the remote call is reached.
        var world = new World();
        world.Classrooms.Member(ClassroomId, Student);
        world.Classrooms.Unreachable = true;

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => world.Service.SendChatMessageAsync(SessionId, Student, "Ammar", "hello", default));

        world.PutInRoom(Student);
        await world.Service.SendChatMessageAsync(SessionId, Student, "Ammar", "hello", default);
        Assert.Single(world.Chat.Rows);
    }

    [Fact]
    public async Task An_ended_lecture_is_refused_before_anyone_is_asked_about()
    {
        // Already true and worth keeping: the "stream is not active" guard comes first, so a session
        // nobody can post to does not cost a membership lookup per attempt.
        var world = new World(status: StreamStatus.Ended);
        world.Classrooms.Member(ClassroomId, Student);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => world.Service.SendChatMessageAsync(SessionId, Student, "Ammar", "hello", default));

        Assert.Empty(world.Classrooms.Asked);
    }

    // --- the reads -------------------------------------------------------------------------------

    [Fact]
    public async Task A_stranger_cannot_read_the_chat_history()
    {
        var world = new World();

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => world.Service.GetChatHistoryPagedAsync(SessionId, Outsider, 1, 20, default));
    }

    [Fact]
    public async Task A_stranger_cannot_read_the_questions()
    {
        var world = new World();

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => world.Service.GetQuestionsPagedAsync(SessionId, Outsider, 1, 20, default));
    }

    [Fact]
    public async Task A_member_reads_both()
    {
        var world = new World();
        world.PutInRoom(Student);
        await world.Service.SendChatMessageAsync(SessionId, Student, "Ammar", "hello", default);
        await world.Service.AskQuestionAsync(SessionId, Student, "Ammar", "why?", default);

        Assert.Single((await world.Service.GetChatHistoryPagedAsync(SessionId, Student, 1, 20, default)).Items);
        Assert.Single((await world.Service.GetQuestionsPagedAsync(SessionId, Student, 1, 20, default)).Items);
    }

    // --- the live feed ---------------------------------------------------------------------------

    [Fact]
    public async Task A_stranger_cannot_watch_the_live_broadcast()
    {
        // What the SignalR group carries: every chat message, reaction, hand-raise and participant
        // count, live. Subscribing to it was open to any authenticated connection with a session id.
        var world = new World();

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => world.Service.EnsureCanWatchAsync(SessionId, Outsider, default));
    }

    [Fact]
    public async Task A_member_may_watch()
    {
        var world = new World();
        world.Classrooms.Member(ClassroomId, Student);

        await world.Service.EnsureCanWatchAsync(SessionId, Student, default);
    }

    [Fact]
    public async Task Watching_a_session_that_has_no_stream_is_not_found()
    {
        var world = new World();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => world.Service.EnsureCanWatchAsync(Guid.NewGuid(), Student, default));
    }

    [Fact]
    public async Task An_ended_lecture_can_still_be_watched_by_its_own_members()
    {
        // Deliberate, and the opposite of the write paths. The group is also how a client learns the
        // session ENDED; refusing to let a member subscribe to a stream that is no longer Live would
        // mean the one broadcast they most need is the one they cannot receive.
        var world = new World(status: StreamStatus.Ended);
        world.Classrooms.Member(ClassroomId, Student);

        await world.Service.EnsureCanWatchAsync(SessionId, Student, default);
    }

    // --- what was already correct ----------------------------------------------------------------

    [Fact]
    public async Task Only_the_sessions_own_teacher_answers_a_question()
    {
        var world = new World();
        world.PutInRoom(Student);
        await world.Service.AskQuestionAsync(SessionId, Student, "Ammar", "why?", default);
        var question = world.Questions.Rows[0];

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => world.Service.AnswerQuestionAsync(question.Id, Outsider, "because", default));

        await world.Service.AnswerQuestionAsync(question.Id, Teacher, "because", default);
        Assert.True(question.IsAnswered);
    }

    [Fact]
    public async Task Raising_a_hand_requires_a_participant_row_and_that_is_the_whole_check()
    {
        // This method was already safe, and it is worth being explicit about WHY: it never checks
        // membership, it checks for a participant row — and that row can only exist because
        // §7.4d put a membership check on the join. It is safe because of a decision made in
        // another method, so if joining ever stops being gated this becomes open again with nothing
        // here to say so. That is what this test is for.
        var world = new World();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => world.Service.ToggleHandRaiseAsync(SessionId, Outsider, true, default));

        // And membership alone is NOT enough, which is the deliberate difference from chat: you
        // cannot raise a hand in a room you have not entered.
        world.Classrooms.Member(ClassroomId, Student);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => world.Service.ToggleHandRaiseAsync(SessionId, Student, true, default));

        world.PutInRoom(Student);
        await world.Service.ToggleHandRaiseAsync(SessionId, Student, true, default);
        Assert.True(world.Participants.Rows.Single(p => p.UserId == Student).IsHandRaised);
    }

    // --- the world -------------------------------------------------------------------------------

    private sealed class World
    {
        public readonly FakeClassroomInternalClient Classrooms = new();
        public readonly FakeChatRepository Chat = new();
        public readonly FakeQuestionRepository Questions = new();
        public readonly FakeReactionRepository Reactions = new();
        public readonly TrackingParticipantRepository Participants = new();
        public readonly RecordingStreamHubContext Hub = new();
        public readonly InteractionService Service;

        public World(StreamStatus status = StreamStatus.Live)
        {
            var stream = new LiveStream
            {
                Id = StreamId,
                SessionId = SessionId,
                ClassroomId = ClassroomId,
                TeacherId = Teacher,
                Status = status,
                StreamKey = "k",
            };

            Service = new InteractionService(
                Chat, Reactions, Questions, Participants,
                new FakeStreamRepository(stream), Hub, Classrooms,
                new RecordingLogger<InteractionService>());
        }

        /// <summary>Gives someone a participant row — what `POST /join` does, after checking membership.</summary>
        public void PutInRoom(Guid userId) => Participants.Rows.Add(new StreamParticipant
        {
            Id = Guid.NewGuid(),
            StreamId = StreamId,
            UserId = userId,
            JoinedAtUtc = DateTime.UtcNow,
        });
    }
}
