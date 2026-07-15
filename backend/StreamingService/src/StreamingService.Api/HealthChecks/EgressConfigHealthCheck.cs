using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StreamingService.Infrastructure.Configuration;

namespace StreamingService.Api.HealthChecks;

/// <summary>
/// Reports whether recording capture is configured (R-5): LiveKit API key/secret + host (also used
/// to verify webhooks) and the egress S3 target. Non-fatal — a gap is reported as Degraded (not
/// Unhealthy) so the service still serves live streaming without recording.
/// </summary>
public sealed class EgressConfigHealthCheck : IHealthCheck
{
    private readonly LiveKitSettings _liveKit;
    private readonly EgressOptions _egress;

    public EgressConfigHealthCheck(IOptions<LiveKitSettings> liveKit, IOptions<EgressOptions> egress)
    {
        _liveKit = liveKit.Value;
        _egress = egress.Value;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var missing = new List<string>();

        // LiveKit credentials double as the webhook verification key.
        if (string.IsNullOrWhiteSpace(_liveKit.ApiKey)) missing.Add("LiveKit:ApiKey");
        if (string.IsNullOrWhiteSpace(_liveKit.ApiSecret)) missing.Add("LiveKit:ApiSecret (webhook verification key)");
        if (string.IsNullOrWhiteSpace(_liveKit.Host)) missing.Add("LiveKit:Host");

        if (_egress.Enabled && string.IsNullOrWhiteSpace(_egress.S3.Bucket))
        {
            missing.Add("Egress:S3:Bucket");
        }

        var data = new Dictionary<string, object>
        {
            ["egressEnabled"] = _egress.Enabled,
            ["keyTemplateConfigured"] = !string.IsNullOrWhiteSpace(_egress.KeyTemplate),
        };

        if (missing.Count > 0)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Recording capture config incomplete: {string.Join(", ", missing)}.", data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Recording capture configured.", data));
    }
}
