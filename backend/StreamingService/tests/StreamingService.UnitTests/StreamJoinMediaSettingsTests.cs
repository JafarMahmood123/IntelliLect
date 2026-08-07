using System.Reflection;
using StreamingService.Application.Abstractions;
using StreamingService.Application.DTOs;
using StreamingService.Application.Services;
using StreamingService.Domain.Entities;
using StreamingService.Domain.Enums;

namespace StreamingService.UnitTests;

/// <summary>
/// The media configuration that travels with the join token — work-plan §7.4's "media config
/// beyond the token", and the only part of `StreamService` that had no coverage.
///
/// `MediaOptionsTests` already pins how the "Media" section BINDS. This pins that what was bound
/// actually reaches the browser, which is a separate failure with no symptom: a value dropped in
/// the mapping does not error, it silently leaves livekit-client on its own default. Per the
/// binding tests' own reasoning, that means a thumbnail-sized tile pulling a full-resolution
/// stream, or a single failed reconnect ejecting a student from a live lecture.
///
/// Several of these are frozen when the browser constructs its Room, so a setting that does not
/// arrive cannot be corrected later in the session.
/// </summary>
public sealed class StreamJoinMediaSettingsTests
{
    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly Guid ClassroomId = Guid.NewGuid();
    private static readonly Guid TeacherId = Guid.NewGuid();
    private static readonly Guid StudentId = Guid.NewGuid();

    /// <summary>Every setting given a value distinct from every other, and from any plausible
    /// default, so a mapping that crosses two fields cannot pass by coincidence.</summary>
    private sealed class DistinctMediaSettings : IMediaSettings
    {
        public bool AdaptiveStream => true;
        public bool Dynacast => true;
        public bool Simulcast => false;
        public string VideoCodec => "av1";
        public string AudioPreset => "musicHighQualityStereo";
        public bool Dtx => false;
        public bool Red => true;
        public bool StopMicTrackOnMute => false;
        public int VideoWidth => 1281;
        public int VideoHeight => 721;
        public int VideoFramerate => 31;
        public int ScreenShareWidth => 1921;
        public int ScreenShareHeight => 1081;
        public int ScreenShareFramerate => 16;
        public int ScreenShareMaxBitrate => 3_000_001;
        public int MaxRetries => 7;
        public int PeerConnectionTimeoutMs => 15_001;
        public int WebsocketTimeoutMs => 15_002;
    }

    private sealed class StubStreamSettings : IStreamSettings
    {
        public string LiveKitHost => "wss://livekit.example";
    }

    private sealed class StubMediaProvider : IMediaProvider
    {
        public string GenerateJoinToken(
            Guid sessionId, Guid userId, string role, string displayName,
            bool canPublishAudio, bool canPublishVideo) => "join-token";
    }

    private static LiveStream Stream(StreamStatus status = StreamStatus.Live) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = SessionId,
        ClassroomId = ClassroomId,
        TeacherId = TeacherId,
        Status = status,
        StreamKey = "k",
        StudentsCanPublishAudio = false,
        StudentsCanPublishVideo = false,
    };

    private static StreamService Create(FakeStreamRepository repo, IMediaSettings? media = null)
        => new(
            repo,
            participantRepository: null!,
            new RecordingStreamHubContext(),
            new StubMediaProvider(),
            new FakeRoomLifecycleService(),
            recordingStarter: null!,
            recordingEgress: null!,
            new StubStreamSettings(),
            media ?? new DistinctMediaSettings(),
            // Both people these tests join as are members. The refusal cases live in
            // StreamJoinAuthorizationTests; here the point is what a legitimate join receives.
            new FakeClassroomInternalClient().Member(ClassroomId, StudentId).Teacher(ClassroomId, TeacherId),
            new RecordingLogger<StreamService>());

    /// <summary>Joins as the student by default; pass <see cref="TeacherId"/> for the owner.</summary>
    private static Task<StreamResponse> JoinAsync(FakeStreamRepository repo, Guid? asUser = null)
        => Create(repo).GetStreamBySessionIdAsync(SessionId, asUser ?? StudentId, "Ammar", default);

    // --- the rule ------------------------------------------------------------------

    [Fact]
    public async Task Every_media_setting_reaches_the_browser_with_the_configured_value()
    {
        // A rule over the interface rather than eighteen assertions: adding a nineteenth setting
        // and forgetting to map it fails here and names it, instead of shipping a value the
        // client never receives.
        var settings = new DistinctMediaSettings();
        var response = await JoinAsync(new FakeStreamRepository(Stream()));

        var mismatched = new List<string>();
        foreach (var property in typeof(IMediaSettings).GetProperties(
                     BindingFlags.Public | BindingFlags.Instance))
        {
            var onResponse = typeof(MediaSettingsResponse).GetProperty(property.Name);
            if (onResponse is null)
            {
                mismatched.Add($"{property.Name}: not present on MediaSettingsResponse");
                continue;
            }

            var expected = property.GetValue(settings);
            var actual = onResponse.GetValue(response.Media);
            if (!Equals(expected, actual))
            {
                mismatched.Add($"{property.Name}: expected {expected}, got {actual}");
            }
        }

        Assert.Empty(mismatched);
    }

    [Fact]
    public void The_response_carries_nothing_the_settings_do_not_define()
    {
        // The other direction. A field on the response with no source is one the browser will read
        // as a configured value while nothing configures it.
        var settingNames = typeof(IMediaSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        var extra = typeof(MediaSettingsResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.DeclaringType == typeof(MediaSettingsResponse))
            .Select(p => p.Name)
            .Where(name => !settingNames.Contains(name))
            .ToList();

        Assert.Empty(extra);
    }

    // --- the settings this feature exists for ---------------------------------------

    [Fact]
    public async Task The_reconnection_settings_arrive_intact()
    {
        // Called out separately because they are the ones with a person on the other end: at
        // livekit-client's default of one retry, a student whose wifi blinked during a lecture is
        // dropped and has to rejoin. They are also frozen at connect time, so if they do not
        // arrive with the token there is no second chance to send them.
        var response = await JoinAsync(new FakeStreamRepository(Stream()));

        Assert.Equal(7, response.Media.MaxRetries);
        Assert.Equal(15_001, response.Media.PeerConnectionTimeoutMs);
        Assert.Equal(15_002, response.Media.WebsocketTimeoutMs);
    }

    [Fact]
    public async Task The_settings_the_assistant_depends_on_arrive_intact()
    {
        // Dtx and StopMicTrackOnMute feed the assistant's audio frame stream and its pause
        // detection. Wrong here, the symptom is the assistant missing idea boundaries — which
        // reads as broken boundary detection rather than as a media misconfiguration.
        var response = await JoinAsync(new FakeStreamRepository(Stream()));

        Assert.False(response.Media.Dtx);
        Assert.False(response.Media.StopMicTrackOnMute);
    }

    [Fact]
    public async Task Every_participant_gets_the_same_media_configuration()
    {
        // The room is one negotiation. A teacher and a student on different codecs or simulcast
        // settings is a call where one of them cannot see the other.
        var repo = new FakeStreamRepository(Stream());

        var asTeacher = await JoinAsync(repo, TeacherId);
        var asStudent = await JoinAsync(repo, StudentId);

        Assert.Equal(asTeacher.Media, asStudent.Media);
    }

    // --- the rest of the join response ----------------------------------------------

    [Fact]
    public async Task Joining_carries_the_token_the_host_and_the_current_publish_policy()
    {
        // Everything the browser needs to connect, in one response — the client has no second
        // call to make, so anything missing here is a room it cannot construct.
        var repo = new FakeStreamRepository(Stream());

        var response = await JoinAsync(repo);

        Assert.Equal("join-token", response.JoinToken);
        Assert.Equal("wss://livekit.example", response.LiveKitHost);
        Assert.False(response.StudentsCanPublishAudio);
        Assert.False(response.StudentsCanPublishVideo);
        Assert.Equal(nameof(StreamStatus.Live), response.Status);
    }

    [Fact]
    public async Task A_session_that_is_over_yields_no_join_token_at_all()
    {
        // LiveKit re-creates a room on demand for any valid token, so issuing one after the
        // session ended would let an evicted student reload the page and land in a freshly
        // created room — alone, in a class that finished.
        var repo = new FakeStreamRepository(Stream(StreamStatus.Ended));

        await Assert.ThrowsAsync<InvalidOperationException>(() => JoinAsync(repo));
    }

    [Fact]
    public async Task Not_even_the_teacher_gets_a_token_once_the_session_is_over()
    {
        // The same rule, and worth its own case: a teacher-shaped exception here would re-create
        // the room for real, and the students' stale tokens would then work again.
        var repo = new FakeStreamRepository(Stream(StreamStatus.Ended));

        await Assert.ThrowsAsync<InvalidOperationException>(() => JoinAsync(repo, TeacherId));
    }

    [Fact]
    public async Task Joining_a_session_that_has_no_stream_is_reported_as_not_found()
    {
        var repo = new FakeStreamRepository();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => JoinAsync(repo));
    }
}
