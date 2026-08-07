using System.Reflection;
using StreamingService.Application.Abstractions;
using StreamingService.Application.DTOs;
using StreamingService.Application.Services;
using StreamingService.Domain.Entities;
using StreamingService.Domain.Enums;

namespace StreamingService.UnitTests;

/// <summary>
/// Who may be handed a LiveKit join token — test-plan G-02, and the worst thing the tenancy sweep
/// found.
///
/// `GET /api/streams/{sessionId}` sat behind a bare `[Authorize]`. The service checked that the
/// stream existed and that it was still Live, and **nothing at all about the caller**. So any
/// account in the platform could name any live session and receive a token for it.
///
/// That is not one step of several. The token IS the authorization for the media room — §7.4's own
/// note puts it plainly: *"once LiveKit holds it our code is never consulted again"*. There was no
/// second place this could have been caught, and no log of it either.
///
/// And it was worse for a teacher, because publishing rights were computed from the caller's own
/// role claim:
///
///     bool isTeacher = role.Equals("Teacher", StringComparison.OrdinalIgnoreCase);
///     bool canPublishAudio = isTeacher || stream.StudentsCanPublishAudio;
///
/// `role` came from the requester's token and had nothing to do with this classroom. Any
/// Teacher-role account could walk into any live lecture in the platform **with camera and
/// microphone publishing rights**, and appear in the room as a teacher. The parameter is gone;
/// ownership of the classroom decides now.
///
/// StreamingService holds no roster, so membership is asked of ClassroomService over the internal
/// surface. That call fails closed — see <see cref="ClassroomInternalClientTests"/>.
/// </summary>
public sealed class StreamJoinAuthorizationTests
{
    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly Guid ClassroomId = Guid.NewGuid();
    private static readonly Guid Teacher = Guid.NewGuid();
    private static readonly Guid Student = Guid.NewGuid();
    private static readonly Guid Outsider = Guid.NewGuid();

    // --- the token ------------------------------------------------------------------------------

    [Fact]
    public async Task A_non_member_gets_no_join_token()
    {
        var world = new World();

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => world.TokenFor(Outsider));

        // And nothing was minted along the way — a token created and then discarded would still be
        // a valid credential if it ever reached a log or a response on some other path.
        Assert.Empty(world.Media.Minted);
    }

    [Fact]
    public async Task A_teacher_of_another_classroom_gets_no_join_token()
    {
        // The case the role claim made dangerous rather than merely leaky. This caller holds a
        // genuine Teacher role; what they do not hold is this classroom.
        var world = new World();

        await Assert.ThrowsAsync<ForbiddenAccessException>(() => world.TokenFor(Outsider));
        Assert.Empty(world.Media.Minted);
    }

    [Fact]
    public async Task An_enrolled_student_gets_a_token_with_the_streams_current_policy()
    {
        var world = new World();
        world.Classrooms.Member(ClassroomId, Student);

        var response = await world.TokenFor(Student);

        Assert.False(string.IsNullOrWhiteSpace(response.JoinToken));
        var minted = Assert.Single(world.Media.Minted);
        Assert.Equal(Student, minted.UserId);
        Assert.Equal("Student", minted.Role);
        // The stream in this world has both student publish switches off.
        Assert.False(minted.CanPublishAudio);
        Assert.False(minted.CanPublishVideo);
    }

    [Fact]
    public async Task A_student_the_teacher_has_unmuted_may_publish()
    {
        // The vacuum guard on the case above: if the policy were ignored and everyone were pinned
        // to "cannot publish", that test would pass and the mute switch would be broken.
        var world = new World(studentsCanPublishAudio: true);
        world.Classrooms.Member(ClassroomId, Student);

        await world.TokenFor(Student);

        var minted = Assert.Single(world.Media.Minted);
        Assert.True(minted.CanPublishAudio);
        Assert.False(minted.CanPublishVideo);
    }

    [Fact]
    public async Task The_classrooms_own_teacher_publishes_freely()
    {
        var world = new World();
        world.Classrooms.Teacher(ClassroomId, Teacher);

        await world.TokenFor(Teacher);

        var minted = Assert.Single(world.Media.Minted);
        Assert.Equal("Teacher", minted.Role);
        Assert.True(minted.CanPublishAudio);
        Assert.True(minted.CanPublishVideo);
    }

    [Fact]
    public async Task Publishing_rights_follow_the_classroom_and_not_the_callers_role_claim()
    {
        // The defect, stated as a rule. This caller is the owning teacher as far as the stream is
        // concerned; nothing about their token is consulted. The mirror case — a Teacher-role
        // account that does not own the classroom — is the refusal above, and between them there
        // is no path left by which a claim can grant a microphone.
        var world = new World();
        world.Classrooms.Member(ClassroomId, Teacher);   // reported as a member, NOT as the teacher

        await world.TokenFor(Teacher);

        // Still a teacher, because the STREAM says so. The classroom's answer decides membership;
        // ownership of the stream decides rights.
        var minted = Assert.Single(world.Media.Minted);
        Assert.Equal("Teacher", minted.Role);
        Assert.True(minted.CanPublishAudio);
    }

    [Fact]
    public void The_token_path_no_longer_accepts_a_role_from_its_caller()
    {
        // Structural, and the reason it is worth a test of its own: the fix was not "check the role
        // more carefully", it was removing the parameter through which the wrong answer arrived. A
        // future edit that re-adds it would compile, pass every case above by ignoring it, and
        // reopen the defect the moment somebody wired it back to `isTeacher`.
        var method = typeof(IStreamService).GetMethod(nameof(IStreamService.GetStreamBySessionIdAsync))!;

        Assert.DoesNotContain(method.GetParameters(), p => p.Name == "role");
        Assert.Contains(method.GetParameters(), p => p.Name == "userId");
    }

    [Fact]
    public async Task Membership_is_checked_before_the_token_is_minted()
    {
        // Ordering, because the cheap read is the one that already existed. If the token were
        // generated first and the check applied after, a refusal would still have produced a
        // credential — and the LiveKit SDK's token is valid whether or not we return it.
        var world = new World();

        await Assert.ThrowsAsync<ForbiddenAccessException>(() => world.TokenFor(Outsider));

        Assert.Contains((ClassroomId, Outsider), world.Classrooms.Asked);
        Assert.Empty(world.Media.Minted);
    }

    [Fact]
    public async Task An_unreachable_classroom_service_refuses_the_token()
    {
        // Fail closed, end to end. The client turns every failure into "no"; this pins that the
        // service does not then treat "no" as "carry on".
        var world = new World();
        world.Classrooms.Member(ClassroomId, Student);
        world.Classrooms.Unreachable = true;

        await Assert.ThrowsAsync<ForbiddenAccessException>(() => world.TokenFor(Student));
        Assert.Empty(world.Media.Minted);
    }

    [Fact]
    public async Task An_ended_session_is_refused_before_the_classroom_is_asked()
    {
        // Already true and worth keeping true: the "this session has ended" refusal predates all of
        // this, and it must not become a remote call per request on a session nobody can join.
        var world = new World(status: StreamStatus.Ended);
        world.Classrooms.Member(ClassroomId, Student);

        await Assert.ThrowsAsync<InvalidOperationException>(() => world.TokenFor(Student));
        Assert.Empty(world.Classrooms.Asked);
    }

    // --- the roster -----------------------------------------------------------------------------

    [Fact]
    public async Task A_non_member_cannot_join_the_roster()
    {
        // A separate endpoint from the token, and separately unguarded. This one writes the
        // participant row the teacher's screen counts and that hand-raise and chat look up, so a
        // stranger could appear in a lecture's roster even where they could not speak.
        var world = new World();

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => world.Service.JoinStreamAsync(SessionId, Outsider, default));

        Assert.Empty(world.Participants.Rows);
        Assert.Empty(world.Hub.ParticipantCounts);
    }

    [Fact]
    public async Task An_enrolled_student_joins_normally()
    {
        var world = new World();
        world.Classrooms.Member(ClassroomId, Student);

        await world.Service.JoinStreamAsync(SessionId, Student, default);

        Assert.Single(world.Participants.Rows);
        Assert.Equal(1, Assert.Single(world.Hub.ParticipantCounts).Count);
    }

    [Fact]
    public async Task An_unreachable_classroom_service_refuses_the_join()
    {
        var world = new World();
        world.Classrooms.Member(ClassroomId, Student);
        world.Classrooms.Unreachable = true;

        await Assert.ThrowsAsync<ForbiddenAccessException>(
            () => world.Service.JoinStreamAsync(SessionId, Student, default));

        Assert.Empty(world.Participants.Rows);
    }

    // --- the status the browser sees ------------------------------------------------------------

    [Fact]
    public void A_refusal_is_403_and_not_401()
    {
        // Not pedantry. The front-end's axios interceptor reads a 401 as "the access token
        // expired": it refreshes the session — which ROTATES the refresh token — replays the
        // request, and is refused again. So every refused join spent a rotation to arrive at the
        // same answer, and a refresh that failed during it would have signed the user out and sent
        // them to /login for clicking on a lecture they are not enrolled in.
        //
        // This service mapped every refusal to 401 because it had nothing else to throw;
        // ClassroomService has carried a ForbiddenAccessException since §7.2.
        var handler = File.ReadAllText(Path.Combine(
            ServiceRoot(), "src", "StreamingService.Api", "Middleware", "GlobalExceptionHandler.cs"));

        Assert.Matches(@"ForbiddenAccessException\s*\r?\n?\s*=>\s*\(StatusCodes.Status403Forbidden", handler);
    }

    [Fact]
    public void No_service_refuses_a_permitted_user_with_a_401()
    {
        // The rule over the two services rather than the two lines. "Only the teacher can …" is an
        // authorization refusal in every case; 401 means "I do not know who you are", which is
        // false by the time any of these run. The remaining UnauthorizedAccessException throws are
        // the claims parsers, where the caller genuinely is not identified.
        var offenders = Directory
            .EnumerateFiles(Path.Combine(ServiceRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(path => File.ReadAllText(path)
                .Contains("throw new UnauthorizedAccessException(\"Only "))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These refuse a permitted-user check with 401, which the browser retries as an expired "
            + "token: " + string.Join(", ", offenders));
    }

    private static string ServiceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, "src", "StreamingService.Application")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    // --- the world ------------------------------------------------------------------------------

    /// <summary>Records what it was asked to mint, so a refused request can be shown to mint nothing.</summary>
    private sealed class RecordingMediaProvider : IMediaProvider
    {
        public readonly List<(Guid SessionId, Guid UserId, string Role, bool CanPublishAudio, bool CanPublishVideo)>
            Minted = [];

        public string GenerateJoinToken(
            Guid sessionId, Guid userId, string role, string displayName,
            bool canPublishAudio, bool canPublishVideo)
        {
            Minted.Add((sessionId, userId, role, canPublishAudio, canPublishVideo));
            return "join-token";
        }
    }

    private sealed class StubStreamSettings : IStreamSettings
    {
        public string LiveKitHost => "wss://livekit.example";
    }

    /// <summary>Values are irrelevant here — what reaches the browser is StreamJoinMediaSettingsTests'.</summary>
    private sealed class StubMediaSettings : IMediaSettings
    {
        public bool AdaptiveStream => true;
        public bool Dynacast => true;
        public bool Simulcast => true;
        public string VideoCodec => "vp8";
        public string AudioPreset => "music";
        public bool Dtx => true;
        public bool Red => true;
        public bool StopMicTrackOnMute => false;
        public int VideoWidth => 1920;
        public int VideoHeight => 1080;
        public int VideoFramerate => 30;
        public int ScreenShareWidth => 1920;
        public int ScreenShareHeight => 1080;
        public int ScreenShareFramerate => 5;
        public int ScreenShareMaxBitrate => 3_000_000;
        public int MaxRetries => 5;
        public int PeerConnectionTimeoutMs => 15_000;
        public int WebsocketTimeoutMs => 15_000;
    }

    private sealed class World
    {
        public readonly FakeClassroomInternalClient Classrooms = new();
        public readonly RecordingMediaProvider Media = new();
        public readonly RecordingStreamHubContext Hub = new();
        public readonly TrackingParticipantRepository Participants = new();
        public readonly StreamService Service;

        public World(
            StreamStatus status = StreamStatus.Live,
            bool studentsCanPublishAudio = false)
        {
            var stream = new LiveStream
            {
                Id = Guid.NewGuid(),
                SessionId = SessionId,
                ClassroomId = ClassroomId,
                TeacherId = Teacher,
                Status = status,
                StreamKey = "k",
                StudentsCanPublishAudio = studentsCanPublishAudio,
                StudentsCanPublishVideo = false,
            };

            Service = new StreamService(
                new FakeStreamRepository(stream),
                Participants,
                Hub,
                Media,
                new FakeRoomLifecycleService(),
                recordingStarter: null!,
                recordingEgress: null!,
                new StubStreamSettings(),
                new StubMediaSettings(),
                Classrooms,
                new RecordingLogger<StreamService>());
        }

        public Task<StreamResponse> TokenFor(Guid userId)
            => Service.GetStreamBySessionIdAsync(SessionId, userId, "Ammar", default);
    }
}
