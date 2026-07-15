using ClassroomService.Application.Abstractions;

namespace ClassroomService.Infrastructure.Configuration;

/// <summary>
/// Recording download options (R-3), bound from the "Recordings" section. The recording objects
/// live in the same S3 bucket as classroom files, so the existing <c>S3Settings</c> supplies the
/// bucket/credentials; only the URL TTL is configured here.
/// </summary>
public sealed class RecordingsOptions : IRecordingDownloadSettings
{
    public const string SectionName = "Recordings";

    /// <summary>Pre-signed URL lifetime in seconds. Kept short (default 600).</summary>
    public int DownloadUrlTtlSeconds { get; init; } = 600;
}
