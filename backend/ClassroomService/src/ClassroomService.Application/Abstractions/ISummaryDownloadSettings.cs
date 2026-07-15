namespace ClassroomService.Application.Abstractions;

/// <summary>
/// Download-URL tuning the summary service needs, surfaced as an interface so the service does not
/// depend on IOptions/Infrastructure. Bound from the "Summaries" config section
/// (env: <c>Summaries__DownloadUrlTtlSeconds</c>). Mirrors <see cref="IRecordingDownloadSettings"/>.
/// </summary>
public interface ISummaryDownloadSettings
{
    /// <summary>How long a minted pre-signed summary URL stays valid. Kept short (default 600s).</summary>
    int DownloadUrlTtlSeconds { get; }
}
