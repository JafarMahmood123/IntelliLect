using ClassroomService.Application.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ClassroomService.Presentation.Filters;

/// <summary>
/// Applies the configured upload ceiling to a request BEFORE its body is read.
///
/// A resource filter rather than <c>[RequestSizeLimit]</c> because that attribute takes a
/// compile-time constant, and the whole point of this work is one configurable value. Resource
/// filters run ahead of model binding, which is the last moment at which an oversized body can be
/// refused without first buffering it — the difference between rejecting a 2 GB upload at the
/// first packet and rejecting it after transferring all of it.
///
/// Two guards, deliberately different:
///   * Kestrel's per-request ceiling is raised to the configured limit (its 30 MB default would
///     otherwise cap us below it) and enforced as the bytes actually arrive, so a LYING or absent
///     Content-Length cannot get past it.
///   * The declared Content-Length is checked up front, so an honest client gets a typed
///     ProblemDetails instead of a mid-stream connection reset.
///
/// Both compare against max + multipart overhead, because Content-Length covers the whole
/// multipart envelope. The exact per-file check belongs to the file service.
/// </summary>
public sealed class UploadSizeLimitFilter : IAsyncResourceFilter
{
    private readonly IUploadSettings _settings;

    public UploadSizeLimitFilter(IUploadSettings settings)
    {
        _settings = settings;
    }

    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext context,
        ResourceExecutionDelegate next)
    {
        var ceiling = _settings.MaxFileSizeBytes + _settings.MultipartOverheadBytes;

        // Read-only once the body has started being read, or when the server does not support
        // per-request limits (the test host). Not being able to raise it is not fatal: the
        // service-level check still refuses the file.
        var sizeFeature = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = ceiling;
        }

        var declaredLength = context.HttpContext.Request.ContentLength;
        if (declaredLength is not null && declaredLength > ceiling)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status413PayloadTooLarge,
                Title = "Payload Too Large",
                Detail = $"The file exceeds the maximum upload size of {_settings.MaxFileSizeBytes} bytes.",
                Instance = context.HttpContext.Request.Path,
            })
            {
                StatusCode = StatusCodes.Status413PayloadTooLarge,
            };
            return;
        }

        await next();
    }
}
