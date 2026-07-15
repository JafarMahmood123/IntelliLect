using ClassroomService.Application.Abstractions;

namespace ClassroomService.Infrastructure.Configuration;

/// <summary>
/// Session-summary download options, bound from the "Summaries" section
/// (env: <c>Summaries__DownloadUrlTtlSeconds</c>). Summary artifacts live in the same S3 bucket as
/// recordings/files, so <c>S3Settings</c> supplies the bucket/credentials; only the URL TTL is new.
/// Mirrors <see cref="RecordingsOptions"/>.
/// </summary>
public sealed class SummariesOptions : ISummaryDownloadSettings
{
    public const string SectionName = "Summaries";

    /// <summary>Pre-signed URL lifetime in seconds. Kept short (default 600).</summary>
    public int DownloadUrlTtlSeconds { get; init; } = 600;
}
