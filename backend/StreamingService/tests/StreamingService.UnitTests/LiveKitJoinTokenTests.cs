using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamingService.Infrastructure.Configuration;
using StreamingService.Infrastructure.Services;

namespace StreamingService.UnitTests;

/// <summary>
/// The LiveKit join token — test-plan area G, work-plan §7.4 "token/role issuance".
///
/// This token IS the authorization for the media room. Once LiveKit has it, nothing in our code
/// is consulted again: the grants inside decide whether someone can turn on a microphone in a
/// live class. It is minted here, so it is verified here.
///
/// The token is decoded rather than verified — the signature is the SDK's job, and these cases
/// are about what we asked it to sign.
/// </summary>
public sealed class LiveKitJoinTokenTests
{
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static LiveKitMediaProvider Provider() => new(
        Options.Create(new LiveKitSettings
        {
            ApiKey = "devkey",
            // The SDK signs with this; it never leaves the test.
            ApiSecret = "a-test-secret-of-at-least-32-characters",
        }),
        NullLogger<LiveKitMediaProvider>.Instance);

    /// <summary>The token's payload, without checking its signature.</summary>
    private static JsonElement Payload(string jwt)
    {
        var segment = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        segment = segment.PadRight(segment.Length + (4 - segment.Length % 4) % 4, '=');
        return JsonDocument.Parse(Convert.FromBase64String(segment)).RootElement;
    }

    private static JsonElement Grants(string jwt) => Payload(jwt).GetProperty("video");

    private static string[] Sources(JsonElement grants)
        => grants.TryGetProperty("canPublishSources", out var list)
            ? list.EnumerateArray().Select(x => x.GetString()!).ToArray()
            : [];

    private static string Token(
        string role, bool audio = true, bool video = true, string displayName = "Amina")
        => Provider().GenerateJoinToken(SessionId, UserId, role, displayName, audio, video);

    [Fact]
    public void A_view_only_student_cannot_publish_anything()
    {
        // The case that matters most. A student with neither permission must come back with the
        // master switch off AND an empty source list — either one alone would leave a way in if
        // LiveKit ever read only the other.
        var grants = Grants(Token("Student", audio: false, video: false));

        Assert.False(grants.GetProperty("canPublish").GetBoolean());
        Assert.Empty(Sources(grants));
    }

    [Fact]
    public void A_student_gets_exactly_the_sources_they_were_granted()
    {
        var grants = Grants(Token("Student", audio: true, video: true));

        Assert.True(grants.GetProperty("canPublish").GetBoolean());
        Assert.Equal(["camera", "microphone"], Sources(grants).Order());
    }

    [Theory]
    [InlineData(true, false, "camera")]
    [InlineData(false, true, "microphone")]
    public void One_permission_grants_one_source(bool video, bool audio, string expected)
    {
        // A student allowed to speak but not be seen must not receive a camera source as a
        // by-product of the master switch being on.
        var grants = Grants(Token("Student", audio: audio, video: video));

        Assert.Equal([expected], Sources(grants));
        Assert.True(grants.GetProperty("canPublish").GetBoolean());
    }

    [Fact]
    public void A_student_never_gets_screen_share_even_with_full_permissions()
    {
        // Screen share is the teacher's, and it is not implied by "can publish video".
        var sources = Sources(Grants(Token("Student", audio: true, video: true)));

        Assert.DoesNotContain("screen_share", sources);
        Assert.DoesNotContain("screen_share_audio", sources);
    }

    [Fact]
    public void A_teacher_gets_screen_share_on_top_of_their_own_permissions()
    {
        var sources = Sources(Grants(Token("Teacher", audio: true, video: true)));

        Assert.Contains("screen_share", sources);
        Assert.Contains("screen_share_audio", sources);
        Assert.Contains("camera", sources);
    }

    [Fact]
    public void A_teacher_keeps_screen_share_even_with_camera_and_microphone_off()
    {
        // Presenting slides with the camera off is an ordinary way to teach, so the master switch
        // must stay on for a teacher who has published nothing else.
        var grants = Grants(Token("Teacher", audio: false, video: false));

        Assert.True(grants.GetProperty("canPublish").GetBoolean());
        Assert.Equal(["screen_share", "screen_share_audio"], Sources(grants).Order());
    }

    [Fact]
    public void The_role_check_is_case_insensitive()
    {
        // The role arrives as a claim from another service. A casing change upstream must not
        // silently demote a teacher to a student's grants.
        Assert.Contains("screen_share", Sources(Grants(Token("teacher"))));
        Assert.Contains("screen_share", Sources(Grants(Token("TEACHER"))));
    }

    [Fact]
    public void An_unknown_role_is_treated_as_a_student()
    {
        // Fail toward fewer rights, not more.
        Assert.DoesNotContain("screen_share", Sources(Grants(Token("Registrar"))));
    }

    [Fact]
    public void The_room_is_the_session_and_the_identity_is_the_user()
    {
        // Identity is what correlates a LiveKit participant with our records; the room is what
        // stops a token for one class opening another.
        var payload = Payload(Token("Student"));

        Assert.Equal(SessionId.ToString(), Grants(Token("Student")).GetProperty("room").GetString());
        Assert.Equal(UserId.ToString(), payload.GetProperty("sub").GetString());
    }

    [Fact]
    public void The_role_travels_in_participant_metadata()
    {
        // The client reads this to decide who is visible to whom — students see only the teacher.
        var metadata = Payload(Token("Teacher")).GetProperty("metadata").GetString();

        Assert.Equal("Teacher", JsonDocument.Parse(metadata!).RootElement.GetProperty("role").GetString());
    }

    [Fact]
    public void A_blank_display_name_falls_back_to_the_user_id()
    {
        // Better a GUID on the tile than an empty label nobody can attribute.
        Assert.Equal(UserId.ToString(), Payload(Token("Student", displayName: "  ")).GetProperty("name").GetString());
    }

    [Fact]
    public void Everyone_may_subscribe_and_send_data()
    {
        // Subscribing is how you attend; the data channel carries chat, quizzes and the
        // assistant's private feedback. Neither depends on publish rights.
        var grants = Grants(Token("Student", audio: false, video: false));

        Assert.True(grants.GetProperty("canSubscribe").GetBoolean());
        Assert.True(grants.GetProperty("canPublishData").GetBoolean());
        Assert.True(grants.GetProperty("roomJoin").GetBoolean());
    }
}
