namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Download-URL tuning the application layer needs, surfaced as an interface so the service does
/// not depend on IOptions/Infrastructure. Bound from the "Recordings" config section.
/// </summary>
public interface IRecordingDownloadSettings
{
    /// <summary>How long a minted pre-signed URL stays valid. Kept short (default 600s).</summary>
    int DownloadUrlTtlSeconds { get; }
}
