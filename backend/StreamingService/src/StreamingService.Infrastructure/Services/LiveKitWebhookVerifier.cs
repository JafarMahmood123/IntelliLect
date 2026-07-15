using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Options;
using StreamingService.Application.Abstractions;
using StreamingService.Infrastructure.Configuration;

namespace StreamingService.Infrastructure.Services;

/// <summary>
/// Verifies LiveKit webhooks using the same API key/secret as token generation. The SDK's
/// <see cref="WebhookReceiver"/> validates the JWT in the Authorization header against a SHA-256
/// checksum of the exact body bytes, so the raw body must be passed through unmodified.
/// </summary>
public sealed class LiveKitWebhookVerifier : ILiveKitWebhookVerifier
{
    private readonly WebhookReceiver _receiver;

    public LiveKitWebhookVerifier(IOptions<LiveKitSettings> settings)
    {
        var value = settings.Value;
        _receiver = new WebhookReceiver(value.ApiKey, value.ApiSecret);
    }

    public WebhookEvent Verify(string body, string authHeader)
    {
        if (string.IsNullOrWhiteSpace(authHeader))
        {
            throw new WebhookVerificationException("Missing LiveKit webhook Authorization header.");
        }

        try
        {
            // skipAuth: false -> enforce the signature; ignoreUnknownFields: true -> tolerate
            // newer LiveKit payload fields.
            return _receiver.Receive(body, authHeader, skipAuth: false, ignoreUnknownFields: true);
        }
        catch (Exception ex) when (ex is not WebhookVerificationException)
        {
            throw new WebhookVerificationException("LiveKit webhook signature verification failed.", ex);
        }
    }
}
