using Livekit.Server.Sdk.Dotnet;

namespace StreamingService.Infrastructure.Services;

/// <summary>
/// Thin seam over the LiveKit <see cref="WebhookReceiver"/> so the recording webhook handler is
/// unit-testable with a canned event and its verification is exercised in isolation.
/// </summary>
public interface ILiveKitWebhookVerifier
{
    /// <summary>Verifies the signed body and returns the parsed event.
    /// Throws <see cref="StreamingService.Application.Abstractions.WebhookVerificationException"/>
    /// if the signature is missing or invalid.</summary>
    WebhookEvent Verify(string body, string authHeader);
}
