using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Options;
using StreamingService.Application.Abstractions;
using StreamingService.Infrastructure.Configuration;
using StreamingService.Infrastructure.Services;

namespace StreamingService.UnitTests;

public sealed class LiveKitWebhookVerifierTests
{
    private const string ApiKey = "devkey";
    private const string ApiSecret = "super_secret_livekit_key_for_development";

    private static LiveKitWebhookVerifier CreateVerifier() =>
        new(Options.Create(new LiveKitSettings { ApiKey = ApiKey, ApiSecret = ApiSecret, Host = "ws://livekit:7880" }));

    private static string SerializeEvent()
    {
        var evt = new WebhookEvent
        {
            Event = "egress_ended",
            EgressInfo = new EgressInfo { EgressId = "EG_1", RoomName = "room", Status = EgressStatus.EgressComplete },
        };
        return JsonFormatter.Default.Format(evt);
    }

    // Reproduces how LiveKit signs a webhook: a JWT whose sha256 claim is the base64 SHA-256 of
    // the exact body bytes, signed with the API secret. Built with the same SDK the receiver uses.
    private static string SignBody(string body, string apiKey = ApiKey, string apiSecret = ApiSecret)
    {
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        return new AccessToken(apiKey, apiSecret)
            .WithSha256(hash)
            .WithTtl(TimeSpan.FromMinutes(5))
            .ToJwt();
    }

    [Fact]
    public void Verify_accepts_a_validly_signed_egress_ended_payload()
    {
        var verifier = CreateVerifier();
        var body = SerializeEvent();
        var auth = SignBody(body);

        var result = verifier.Verify(body, auth);

        Assert.Equal("egress_ended", result.Event);
        Assert.Equal("EG_1", result.EgressInfo.EgressId);
    }

    [Fact]
    public void Verify_rejects_a_missing_authorization_header()
    {
        var verifier = CreateVerifier();
        Assert.Throws<WebhookVerificationException>(() => verifier.Verify(SerializeEvent(), string.Empty));
    }

    [Fact]
    public void Verify_rejects_a_garbage_signature()
    {
        var verifier = CreateVerifier();
        Assert.Throws<WebhookVerificationException>(() => verifier.Verify(SerializeEvent(), "not-a-real-token"));
    }

    [Fact]
    public void Verify_rejects_a_token_signed_with_the_wrong_secret()
    {
        var verifier = CreateVerifier();
        var body = SerializeEvent();
        var auth = SignBody(body, apiSecret: "the-wrong-secret-value-32chars!!");

        Assert.Throws<WebhookVerificationException>(() => verifier.Verify(body, auth));
    }

    [Fact]
    public void Verify_rejects_a_tampered_body()
    {
        var verifier = CreateVerifier();
        var auth = SignBody(SerializeEvent());

        // Same valid token, but the body no longer matches its sha256 claim.
        Assert.Throws<WebhookVerificationException>(() => verifier.Verify(SerializeEvent().Replace("EG_1", "EG_TAMPERED"), auth));
    }
}
