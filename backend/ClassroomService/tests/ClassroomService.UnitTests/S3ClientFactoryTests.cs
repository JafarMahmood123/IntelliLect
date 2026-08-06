using ClassroomService.Infrastructure.Configuration;

namespace ClassroomService.UnitTests;

/// <summary>
/// What a slow or absent MinIO costs, and where the credentials come from (work-plan §10.4).
///
/// Two defects lived in four lines of the composition root, and neither was visible from inside
/// this service:
///
/// The credentials were <b>string literals</b> — <c>"testuser"</c> / <c>"testpassword123!"</c> —
/// while MinIO itself and StreamingService's egress both read the same two values from
/// <c>backend/.env</c>. So the platform worked right up until someone changed the MinIO password,
/// which is the first thing anyone does before a real deployment, and then every upload,
/// download, recording and summary in <i>this service alone</i> failed with an
/// <c>InvalidAccessKeyId</c> against a bucket that plainly exists.
///
/// And neither timeout nor retry count was set, so the AWS SDK's defaults applied: 100 seconds
/// and four retries with backoff. Correct for S3 across the internet, wrong for a container one
/// hop away — a MinIO that was simply <i>down</i> held a user's request for minutes.
/// </summary>
public sealed class S3ClientFactoryTests
{
    private static S3Settings Settings(
        string accessKey = "minio-user",
        string secretKey = "minio-secret",
        int timeoutSeconds = 10,
        int maxErrorRetry = 1) => new()
        {
            BucketName = "intellilect-files",
            ServiceUrl = "http://intellilect-s3:9000",
            AccessKey = accessKey,
            SecretKey = secretKey,
            TimeoutSeconds = timeoutSeconds,
            MaxErrorRetry = maxErrorRetry,
        };

    // --- what a broken MinIO costs ----------------------------------------------------

    [Fact]
    public void A_call_is_bounded_rather_than_left_to_the_sdk_default()
    {
        using var client = S3ClientFactory.Create(Settings(timeoutSeconds: 7), "http://s3:9000");

        Assert.Equal(TimeSpan.FromSeconds(7), client.Config.Timeout);
    }

    [Fact]
    public void The_default_bound_is_seconds_rather_than_the_sdk_hundred()
    {
        // The number matters less than the order of magnitude. 100s was never chosen — it was
        // inherited, and inheriting a wide-area default for a call to the next container is how a
        // dependency being down becomes a request hanging.
        using var client = S3ClientFactory.Create(Settings(timeoutSeconds: 0), "http://s3:9000");

        Assert.Equal(TimeSpan.FromSeconds(S3ClientFactory.DefaultTimeoutSeconds), client.Config.Timeout);
        Assert.True(client.Config.Timeout < TimeSpan.FromSeconds(30), "still an internet-scale timeout");
    }

    [Fact]
    public void Retries_are_bounded_so_a_dead_dependency_fails_once_rather_than_five_times()
    {
        using var client = S3ClientFactory.Create(Settings(maxErrorRetry: 1), "http://s3:9000");

        // The SDK's default is 4, and retries are the multiplier on the timeout above: 4 retries
        // of a 100s call is where "minutes" came from. The two settings are only meaningful
        // together, which is why they moved together.
        Assert.Equal(1, client.Config.MaxErrorRetry);
    }

    [Fact]
    public void Turning_retries_off_is_honoured_rather_than_read_as_unset()
    {
        // Zero is a legitimate choice — the health probe wants exactly this. Treating it as
        // "unset" and restoring the default would silently undo a deliberate decision, and the
        // usual `> 0 ? x : default` idiom used everywhere else in this file does precisely that.
        using var client = S3ClientFactory.Create(Settings(maxErrorRetry: 0), "http://s3:9000");

        Assert.Equal(0, client.Config.MaxErrorRetry);
    }

    // --- where the credentials come from ----------------------------------------------

    [Fact]
    public void Credentials_come_from_settings_rather_than_from_the_source()
    {
        using var client = S3ClientFactory.Create(Settings(accessKey: "rotated-user"), "http://s3:9000");

        // The client does not expose its credentials, so this asserts the only observable
        // consequence: building with a *blank* pair is refused, which it could not have been
        // while the literals were there to fall back on.
        Assert.Throws<InvalidOperationException>(
            () => S3ClientFactory.EnsureCredentialsConfigured(Settings(accessKey: "")));
        Assert.NotNull(client);
    }

    [Theory]
    [InlineData("", "secret")]
    [InlineData("user", "")]
    [InlineData("   ", "   ")]
    [InlineData(null, null)]
    public void Missing_credentials_stop_startup_instead_of_the_first_upload(string? key, string? secret)
    {
        var settings = key is null ? null : Settings(accessKey: key, secretKey: secret!);

        var error = Assert.Throws<InvalidOperationException>(
            () => S3ClientFactory.EnsureCredentialsConfigured(settings));

        // The message has to name the env vars. "S3 is misconfigured" sends the reader to the
        // bucket, the endpoint and the network before the two variables nobody set.
        Assert.Contains("S3Settings__AccessKey", error.Message);
        Assert.Contains("MINIO_ROOT_PASSWORD", error.Message);
    }

    [Fact]
    public void Configured_credentials_start_up_quietly()
    {
        S3ClientFactory.EnsureCredentialsConfigured(Settings());
    }

    // --- the settings that carry all of this ------------------------------------------

    [Fact]
    public void The_shipped_defaults_are_the_safe_ones()
    {
        // A deployment that sets only the bucket and endpoint — which is what this service's
        // compose file did until §10.4 — must still get bounded calls rather than the SDK's.
        var bare = new S3Settings { BucketName = "b", ServiceUrl = "http://s3:9000" };

        Assert.Equal(10, bare.TimeoutSeconds);
        Assert.Equal(1, bare.MaxErrorRetry);
        Assert.Equal(3, bare.HealthProbeTimeoutSeconds);
        // And no credential default at all: the previous one was a literal that happened to match
        // a development MinIO, which is exactly why nobody noticed it was there.
        Assert.Empty(bare.AccessKey);
        Assert.Empty(bare.SecretKey);
    }

    [Fact]
    public void The_health_probe_gets_a_tighter_budget_than_a_real_call()
    {
        // Not a rounding preference. /health is what the smoke suite and the e2e readiness gate
        // poll; if it waits as long as a real upload does, a down MinIO stalls whatever is
        // watching it, and the probe becomes the outage.
        var settings = Settings();

        Assert.True(
            settings.HealthProbeTimeoutSeconds < settings.TimeoutSeconds,
            "the health probe may not wait as long as the operation it is probing");
    }
}
