using ClassroomService.Infrastructure.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ClassroomService.Api.HealthChecks;

/// <summary>
/// Reports whether the summary-download configuration is coherent (S-5): the shared S3 bucket is
/// configured and the pre-signed URL TTL is positive. This is a CONFIG check only — live bucket
/// reachability is already probed by <see cref="RecordingsStorageHealthCheck"/> (summaries live in
/// the same bucket). Non-fatal — a gap is reported as Degraded. Never surfaces secrets.
/// </summary>
public sealed class SummariesConfigHealthCheck : IHealthCheck
{
    private readonly S3Settings _s3Settings;
    private readonly SummariesOptions _summaries;

    public SummariesConfigHealthCheck(IOptions<S3Settings> s3Settings, IOptions<SummariesOptions> summaries)
    {
        _s3Settings = s3Settings.Value;
        _summaries = summaries.Value;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_s3Settings.BucketName))
        {
            return Task.FromResult(
                HealthCheckResult.Degraded("Summaries S3 bucket is not configured (S3Settings:BucketName)."));
        }

        if (_summaries.DownloadUrlTtlSeconds <= 0)
        {
            return Task.FromResult(
                HealthCheckResult.Degraded("Summary download URL TTL is not configured (Summaries:DownloadUrlTtlSeconds)."));
        }

        var data = new Dictionary<string, object>
        {
            ["ttlSeconds"] = _summaries.DownloadUrlTtlSeconds,
            ["bucket"] = _s3Settings.BucketName,
        };

        return Task.FromResult(HealthCheckResult.Healthy("Summary download configuration is valid.", data));
    }
}
