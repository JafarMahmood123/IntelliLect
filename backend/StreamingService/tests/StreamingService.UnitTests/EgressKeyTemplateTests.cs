using StreamingService.Infrastructure.Services;

namespace StreamingService.UnitTests;

public sealed class EgressKeyTemplateTests
{
    [Fact]
    public void Render_substitutes_room_name_and_time()
    {
        var time = new DateTime(2026, 7, 15, 12, 30, 45, DateTimeKind.Utc);

        var key = EgressKeyTemplate.Render("recordings/{room_name}/{time}.mp4", "room-abc", time);

        Assert.Equal("recordings/room-abc/20260715T123045Z.mp4", key);
    }

    [Fact]
    public void Render_normalises_time_to_utc()
    {
        // A local kind is converted to UTC before formatting, so the key is deterministic.
        var utc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var local = utc.ToLocalTime();

        var key = EgressKeyTemplate.Render("{time}", "room-abc", local);

        Assert.Equal("20260102T030405Z", key);
    }

    [Fact]
    public void Render_leaves_templates_without_tokens_unchanged()
    {
        var key = EgressKeyTemplate.Render("static/key.mp4", "room-abc", new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal("static/key.mp4", key);
    }
}
