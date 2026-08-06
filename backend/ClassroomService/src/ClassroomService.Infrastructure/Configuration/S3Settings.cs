namespace ClassroomService.Infrastructure.Configuration;

public class S3Settings
{
    public const string SectionName = "S3Settings";
    public string BucketName { get; init; } = null!;
    public string ServiceUrl { get; init; } = null!;

    /// <summary>
    /// Browser-reachable S3/MinIO endpoint used ONLY to mint pre-signed download URLs. In Docker
    /// the service talks to MinIO over the internal <see cref="ServiceUrl"/> (e.g.
    /// http://intellilect-s3:9000), but the browser must hit the host-published address
    /// (e.g. http://localhost:9000). SigV4 signs the host, so the URL must be signed for the host
    /// the browser will actually use. Presigning is a local operation — nothing connects here.
    /// Falls back to <see cref="ServiceUrl"/> when unset.
    /// </summary>
    public string? PublicServiceUrl { get; init; }

    public string Region { get; init; } = "us-east-1";

    /// <summary>
    /// MinIO credentials. These used to be string literals in the composition root, while every
    /// other component reads the same two values from <c>backend/.env</c> — so changing the MinIO
    /// password, which is the first thing anyone does before a real deployment, broke every
    /// upload, download, recording and summary in this service and nothing else. The failure
    /// surfaces as <c>InvalidAccessKeyId</c> against a bucket that plainly exists.
    /// </summary>
    public string AccessKey { get; init; } = string.Empty;

    /// <inheritdoc cref="AccessKey"/>
    public string SecretKey { get; init; } = string.Empty;

    /// <summary>
    /// How long a single S3 call may take (§10.4). The AWS SDK's own default is <b>100 seconds</b>
    /// — a sensible number for S3 across the internet and a terrible one for MinIO one hop away.
    /// Combined with the SDK's default of four retries, a stopped MinIO holds a user's request for
    /// several minutes rather than failing in one.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// Retries per call. One, not the SDK's four: these calls cross a container network to a
    /// single MinIO with no failover, so a second attempt covers a dropped connection and further
    /// attempts only multiply the wait for an outage that will not clear in eight seconds.
    /// </summary>
    public int MaxErrorRetry { get; init; } = 1;

    /// <summary>
    /// The budget for the <c>/health</c> bucket probe, which must be far tighter than a real
    /// call's. A health endpoint that blocks while its dependency is down turns one sick service
    /// into a stalled orchestrator — the probe becomes the outage.
    /// </summary>
    public int HealthProbeTimeoutSeconds { get; init; } = 3;
}