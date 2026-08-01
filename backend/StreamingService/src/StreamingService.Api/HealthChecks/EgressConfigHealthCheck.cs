using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StreamingService.Application.Abstractions;
using StreamingService.Infrastructure.Configuration;

namespace StreamingService.Api.HealthChecks;

/// <summary>
/// Reports whether recording capture is configured AND alive (R-5): LiveKit API key/secret + host
/// (also used to verify webhooks), the egress S3 target, and a live probe of the egress worker.
/// Non-fatal — every gap is Degraded (not Unhealthy) so the service still serves live streaming
/// without recording.
///
/// The probe exists because config-only checks are blind to the failure that actually happens: the
/// egress worker sat dead for over ten hours (crash-looping against a stopped Redis) while this
/// check reported Healthy, because a bucket name was still present in configuration.
/// </summary>
public sealed class EgressConfigHealthCheck : IHealthCheck
{
    private readonly LiveKitSettings _liveKit;
    private readonly EgressOptions _egress;
    private readonly IRecordingEgressService _recordingEgress;

    public EgressConfigHealthCheck(
        IOptions<LiveKitSettings> liveKit,
        IOptions<EgressOptions> egress,
        IRecordingEgressService recordingEgress)
    {
        _liveKit = liveKit.Value;
        _egress = egress.Value;
        _recordingEgress = recordingEgress;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
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
            ["layout"] = _egress.Layout,
            // Same reasoning as s3Endpoint: this URL is resolved by the egress worker, not by a
            // browser, so a value that looks right from the outside can still be unreachable
            // where it matters — and the symptom is a failed recording after the lesson.
            ["recordingTemplate"] = string.IsNullOrWhiteSpace(_egress.CustomBaseUrl)
                ? "(LiveKit built-in)"
                : _egress.CustomBaseUrl,
            // Surfaced because the wrong value here is invisible otherwise: the egress worker runs
            // on host networking, so a bridge-network service name resolves for THIS service but
            // never for the uploader, and recordings then fail only at the upload step.
            ["s3Endpoint"] = string.IsNullOrWhiteSpace(_egress.S3.Endpoint)
                ? "(default AWS)"
                : _egress.S3.Endpoint,
        };

        if (missing.Count > 0)
        {
            return HealthCheckResult.Degraded(
                $"Recording capture config incomplete: {string.Join(", ", missing)}.", data: data);
        }

        if (!_egress.Enabled)
        {
            return HealthCheckResult.Healthy("Recording disabled by configuration.", data);
        }

        // A successful ListEgress proves a worker is registered and answering — the question worth
        // asking. The count itself is incidental.
        try
        {
            var active = await _recordingEgress.GetActiveEgressIdsAsync(cancellationToken);
            data["activeEgressCount"] = active.Count;
        }
        catch (Exception ex)
        {
            data["egressProbeError"] = ex.Message;
            return HealthCheckResult.Degraded(
                "Recording configured but the egress worker did not respond; recordings will not be "
                + "captured.", ex, data);
        }

        return HealthCheckResult.Healthy("Recording capture configured and egress responding.", data);
    }
}
