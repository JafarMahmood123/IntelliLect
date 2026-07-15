using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using StreamingService.Presentation.Controllers;

namespace StreamingService.UnitTests;

public sealed class LiveKitWebhookControllerTests
{
    private static LiveKitWebhookController CreateController(FakeRecordingWebhookHandler handler, string body, string authHeader)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (authHeader.Length > 0)
        {
            httpContext.Request.Headers.Authorization = authHeader;
        }

        return new LiveKitWebhookController(handler, new RecordingLogger<LiveKitWebhookController>())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    [Fact]
    public async Task Receive_returns_200_and_forwards_body_and_auth_for_a_verified_webhook()
    {
        var handler = new FakeRecordingWebhookHandler();
        var controller = CreateController(handler, "the-raw-body", "Bearer signed-token");

        var result = await controller.Receive(default);

        Assert.IsType<OkResult>(result);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("the-raw-body", handler.LastBody);
        Assert.Equal("Bearer signed-token", handler.LastAuthHeader);
    }

    [Fact]
    public async Task Receive_returns_401_when_the_signature_is_invalid()
    {
        var handler = new FakeRecordingWebhookHandler(throwInvalid: true);
        var controller = CreateController(handler, "body", "bad-token");

        var result = await controller.Receive(default);

        Assert.IsType<UnauthorizedResult>(result);
    }
}
