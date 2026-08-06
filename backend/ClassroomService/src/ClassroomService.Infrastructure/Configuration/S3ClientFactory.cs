using Amazon.S3;

namespace ClassroomService.Infrastructure.Configuration;

/// <summary>
/// Builds the MinIO/S3 clients this service uses (work-plan §10.4).
///
/// Extracted from the composition root so the two things that were wrong with it are directly
/// assertable rather than only observable in a deployment:
///
/// 1. The credentials were <b>string literals</b>, while MinIO itself and StreamingService's
///    egress both read the same two values from <c>backend/.env</c>. Rotating the MinIO password
///    — the first thing anyone does before a real deployment — broke every upload, download,
///    recording and summary in this service and nothing else, with an <c>InvalidAccessKeyId</c>
///    against a bucket that plainly exists.
/// 2. Neither timeout nor retry count was set, so the AWS SDK's defaults applied: 100 seconds and
///    four retries with backoff. Those are reasonable numbers for S3 across the internet and the
///    wrong ones for a MinIO container one hop away — a dependency that was simply <i>down</i>
///    held a user's request for minutes instead of failing in one.
/// </summary>
public static class S3ClientFactory
{
    public const int DefaultTimeoutSeconds = 10;
    public const int DefaultMaxErrorRetry = 1;

    /// <summary>
    /// Creates a client for <paramref name="serviceUrl"/>. Two are built: one for the internal
    /// endpoint (uploads, deletes, byte reads) and one for the browser-reachable endpoint, which
    /// only ever pre-signs and therefore never connects.
    /// </summary>
    public static AmazonS3Client Create(S3Settings settings, string? serviceUrl)
    {
        var config = new AmazonS3Config
        {
            RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(settings.Region),
            ForcePathStyle = true,
            Timeout = TimeSpan.FromSeconds(
                settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : DefaultTimeoutSeconds),
            // Zero is a legitimate choice ("do not retry"), so the fallback triggers on negative
            // values only — treating 0 as "unset" would silently restore retries someone turned off.
            MaxErrorRetry = settings.MaxErrorRetry >= 0 ? settings.MaxErrorRetry : DefaultMaxErrorRetry,
        };

        if (!string.IsNullOrEmpty(serviceUrl))
        {
            config.ServiceURL = serviceUrl;
        }

        return new AmazonS3Client(settings.AccessKey, settings.SecretKey, config);
    }

    /// <summary>
    /// Throws at startup, naming the keys, rather than letting the service boot and fail on its
    /// first upload. There is no safe default for a credential: the previous "default" was a
    /// literal that happened to match a development MinIO, which is precisely why this went
    /// unnoticed — it worked until someone changed the password, and then only here.
    /// </summary>
    public static void EnsureCredentialsConfigured(S3Settings? settings)
    {
        if (!string.IsNullOrWhiteSpace(settings?.AccessKey)
            && !string.IsNullOrWhiteSpace(settings.SecretKey))
        {
            return;
        }

        throw new InvalidOperationException(
            "S3Settings:AccessKey and S3Settings:SecretKey must be configured (compose passes them "
            + "as S3Settings__AccessKey / S3Settings__SecretKey from MINIO_ROOT_USER and "
            + "MINIO_ROOT_PASSWORD in backend/.env). Without them no upload, download, recording "
            + "or summary in this service can work.");
    }
}
