namespace StreamingService.Application.Abstractions;

/// <summary>
/// Verifies and processes a raw LiveKit webhook (R-1). Kept SDK-free so the Presentation-layer
/// endpoint can stay thin: it hands over the raw body + Authorization header and this handler
/// verifies the signature, correlates the egress to a stream, and publishes the
/// recording-ready event. Implemented in Infrastructure over the LiveKit SDK.
/// </summary>
public interface IRecordingWebhookHandler
{
    /// <summary>
    /// Verifies the webhook signature and, for a terminal egress event, publishes a
    /// SessionRecordingReadyMessage. Throws <see cref="WebhookVerificationException"/> on an
    /// invalid signature. Safe to call more than once for the same delivery (idempotent).
    /// </summary>
    Task HandleAsync(string body, string authHeader, CancellationToken ct = default);
}
