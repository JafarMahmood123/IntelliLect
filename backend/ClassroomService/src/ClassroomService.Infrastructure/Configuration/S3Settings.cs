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
}