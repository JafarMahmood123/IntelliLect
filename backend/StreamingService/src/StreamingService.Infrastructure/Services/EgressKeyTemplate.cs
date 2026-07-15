using System.Globalization;

namespace StreamingService.Infrastructure.Services;

/// <summary>
/// Renders the configured <c>Egress:KeyTemplate</c> into a concrete S3 object key.
/// Supported tokens: <c>{room_name}</c> and <c>{time}</c> (UTC, formatted yyyyMMdd'T'HHmmss'Z').
/// Kept pure/static so key rendering is unit-testable without any LiveKit dependency.
/// </summary>
public static class EgressKeyTemplate
{
    public static string Render(string template, string roomName, DateTime timeUtc)
    {
        var time = timeUtc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        return template
            .Replace("{room_name}", roomName)
            .Replace("{time}", time);
    }
}
