using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using StreamingService.Application.Abstractions;

namespace StreamingService.Presentation.Controllers;

/// <summary>
/// Receives LiveKit egress webhooks (R-1). No JWT: authenticity comes from the LiveKit-signed
/// Authorization header, verified inside the handler. Returns 200 quickly for verified webhooks
/// so LiveKit does not retry, and 401 for an invalid signature.
/// </summary>
[ApiController]
[Route("api/webhooks/livekit")]
public sealed class LiveKitWebhookController : ControllerBase
{
    private readonly IRecordingWebhookHandler _handler;
    private readonly ILogger<LiveKitWebhookController> _logger;

    public LiveKitWebhookController(
        IRecordingWebhookHandler handler,
        ILogger<LiveKitWebhookController> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        // Read the raw body: the signature is a checksum over the exact bytes, so no model binding.
        string body;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(ct);
        }

        var authHeader = Request.Headers.Authorization.ToString();

        try
        {
            await _handler.HandleAsync(body, authHeader, ct);
        }
        catch (WebhookVerificationException ex)
        {
            _logger.LogWarning(ex, "Rejected LiveKit webhook with an invalid signature.");
            return Unauthorized();
        }

        return Ok();
    }
}
