namespace StreamingService.Application.Abstractions;

/// <summary>
/// Thrown when a LiveKit webhook cannot be authenticated (missing/invalid signature). The
/// webhook endpoint maps this to 401 and never trusts the payload.
/// </summary>
public sealed class WebhookVerificationException : Exception
{
    public WebhookVerificationException(string message) : base(message) { }
    public WebhookVerificationException(string message, Exception inner) : base(message, inner) { }
}
