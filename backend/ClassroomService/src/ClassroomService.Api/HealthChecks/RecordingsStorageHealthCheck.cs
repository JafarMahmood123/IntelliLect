using Amazon.S3;
using Amazon.S3.Util;
using ClassroomService.Infrastructure.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ClassroomService.Api.HealthChecks;

/// <summary>
/// Reports whether recording storage is usable (R-5): the S3 bucket is configured and reachable
/// with the current credentials (needed to presign download URLs and delete objects). Non-fatal —
/// a gap is reported as Degraded so the rest of ClassroomService stays available.
/// </summary>
public sealed class RecordingsStorageHealthCheck : IHealthCheck
{
    private readonly IAmazonS3 _s3;
    private readonly S3Settings _s3Settings;

    public RecordingsStorageHealthCheck(IAmazonS3 s3, IOptions<S3Settings> s3Settings)
    {
        _s3 = s3;
        _s3Settings = s3Settings.Value;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_s3Settings.BucketName))
        {
            return HealthCheckResult.Degraded("Recordings S3 bucket is not configured (S3Settings:BucketName).");
        }

        var data = new Dictionary<string, object> { ["bucket"] = _s3Settings.BucketName };

        try
        {
            var exists = await AmazonS3Util.DoesS3BucketExistV2Async(_s3, _s3Settings.BucketName);
            return exists
                ? HealthCheckResult.Healthy("Recordings S3 bucket reachable.", data)
                : HealthCheckResult.Degraded("Recordings S3 bucket not found.", data: data);
        }
        catch (Exception ex)
        {
            // Never surface credentials/keys — just the failure class.
            return HealthCheckResult.Degraded("Recordings S3 bucket unreachable.", ex, data);
        }
    }
}
